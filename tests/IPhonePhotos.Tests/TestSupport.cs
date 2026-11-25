using System.Security.Cryptography;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Tests;

/// <summary>Carpetas temporales: un "iPhone" falso (origen) y el destino en el PC.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "iphotos-tests-" + Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "iphone");
    public string Dest => Path.Combine(Root, "pc");

    public Sandbox()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Dest);
    }

    /// <summary>Crea un archivo con contenido aleatorio reproducible (según la semilla) en el "iPhone".</summary>
    public string AddPhoto(string relativePath, int size, int seed = 1)
    {
        var path = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Bytes(size, seed));
        // Fecha estable y distinta por archivo, como haría un iPhone.
        File.SetLastWriteTime(path, new DateTime(2024, 1, 1, 12, 0, 0).AddMinutes(seed * 7 + size % 50));
        return path;
    }

    public void AddLibrary(int count, int baseSize = 4000)
    {
        for (var i = 0; i < count; i++)
        {
            var folder = i < count / 2 ? "DCIM\\100APPLE" : "DCIM\\101APPLE";
            AddPhoto($"{folder}\\IMG_{i + 1:0000}.HEIC", baseSize + i * 37, seed: i + 1);
        }
    }

    public static byte[] Bytes(int size, int seed)
    {
        var rnd = new Random(seed);
        var b = new byte[size];
        rnd.NextBytes(b);
        return b;
    }

    public static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public string DestFile(string relativePath) => Path.Combine(Dest, relativePath);

    public IEnumerable<string> DestFiles() =>
        Directory.EnumerateFiles(Dest, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + ".iphotos" + Path.DirectorySeparatorChar))
            .Where(p => !p.EndsWith(IPhonePhotos.Core.Transfer.TransferSession.LastCopyReportName)); // informe, no es una foto

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* limpieza best-effort */ }
    }
}

/// <summary>
/// Dispositivo de pruebas con fallos programables: cortes de cable, errores transitorios y esperas.
/// Envuelve una carpeta local (como si fuera un iPhone).
/// </summary>
public sealed class FlakyProvider : IDeviceBackendProvider
{
    private readonly LocalFolderProvider _inner;
    private int _polls;

    public FlakyProvider(string sourceFolder, string technique = "Prueba (con fallos)", string key = "flaky",
        int priority = 100, bool isSnapshot = false)
    {
        Technique = technique;
        Key = key;
        Priority = priority;
        IsSnapshot = isSnapshot;
        _inner = new LocalFolderProvider(sourceFolder, "iPhone de pruebas", technique, key, priority, isSnapshot);
    }

    public string Technique { get; }
    public string Key { get; }
    public int Priority { get; }
    public bool IsSnapshot { get; }

    /// <summary>Si es true, abrir esta técnica falla (como un iPhone bloqueado o un controlador roto).</summary>
    public bool FailToOpen { get; set; }

    /// <summary>Si es true, la técnica abre bien pero no muestra ninguna foto (típico de WPD con el iPhone bloqueado).</summary>
    public bool ShowNothing { get; set; }

    /// <summary>¿Está el dispositivo "enchufado"?</summary>
    public bool Present { get; set; } = true;

    /// <summary>Tras este número de consultas con el dispositivo ausente, vuelve solo a aparecer.</summary>
    public int ReappearAfterPolls { get; set; } = 3;

    /// <summary>Se ejecuta cada vez que se abre una conexión (para decidir qué fallos inyectar).</summary>
    public Func<int, FlakyBackend, FlakyBackend>? ConfigureConnection { get; set; }

    public int Opens { get; private set; }

    /// <summary>Copias completadas por archivo (para comprobar que nada se recopia sin necesidad).</summary>
    public Dictionary<string, int> CompletedCopies { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceDescriptor> Discover()
    {
        if (!Present)
        {
            if (++_polls >= ReappearAfterPolls) { Present = true; _polls = 0; }
            else return Array.Empty<DeviceDescriptor>();
        }
        return _inner.Discover().Select(d => d with { Technique = Technique }).ToList();
    }

    public IDeviceBackend Open(DeviceDescriptor device)
    {
        if (!Present) throw new DeviceLostException("ausente");
        if (FailToOpen) throw new InvalidOperationException("no abre (simulado)");
        Opens++;
        var real = _inner.Open(device);
        var flaky = new FlakyBackend(real, this);
        return ConfigureConnection?.Invoke(Opens, flaky) ?? flaky;
    }

    internal void Completed(RemoteFile f)
    {
        CompletedCopies.TryGetValue(f.RelativePath, out var n);
        CompletedCopies[f.RelativePath] = n + 1;
    }
}

public sealed class FlakyBackend : IDeviceBackend
{
    private readonly IDeviceBackend _inner;
    private readonly FlakyProvider _provider;
    private int _copiesStarted;

    public FlakyBackend(IDeviceBackend inner, FlakyProvider provider)
    {
        _inner = inner;
        _provider = provider;
    }

    public bool Dead { get; set; }

    /// <summary>Tras copiar este nº de archivos con éxito, "se desenchufa el cable" en el siguiente.</summary>
    public int? UnplugAfterFiles { get; set; }

    /// <summary>Número de intentos fallidos transitorios por archivo antes de que funcione.</summary>
    public int TransientFailuresPerFile { get; set; }

    /// <summary>Archivos que fallan siempre (no es culpa del cable).</summary>
    public HashSet<string> AlwaysFail { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Si se pone, la copia de ese archivo se queda colgada a la mitad hasta cancelar.</summary>
    public string? HangOn { get; set; }

    private readonly Dictionary<string, int> _failuresSoFar = new(StringComparer.OrdinalIgnoreCase);

    public DeviceDescriptor Device => _inner.Device;
    public BackendCapabilities Capabilities => _inner.Capabilities;

    /// <summary>Si es true, el listado de archivos se corta a la mitad con el dispositivo "desenchufado".</summary>
    public bool DieDuringEnumeration { get; set; }

    public IEnumerable<RemoteFile> EnumerateMedia(CancellationToken ct)
    {
        if (_provider.ShowNothing) yield break;
        var n = 0;
        foreach (var f in _inner.EnumerateMedia(ct))
        {
            if (DieDuringEnumeration && ++n > 2)
            {
                Dead = true;
                _provider.Present = false;
                throw new IOException("cable desenchufado durante el escaneo");
            }
            yield return f;
        }
    }

    public async Task CopyToAsync(RemoteFile file, Stream destination, CancellationToken ct)
    {
        if (Dead) throw new IOException("dispositivo desconectado");

        if (UnplugAfterFiles is { } n && _copiesStarted >= n)
        {
            // Se corta el cable a mitad de este archivo: se escribe media copia y se pierde la conexión.
            var half = new byte[Math.Max(1, (int)(file.Size / 2))];
            await destination.WriteAsync(half, ct);
            Dead = true;
            _provider.Present = false;
            throw new IOException("cable desenchufado");
        }
        _copiesStarted++;

        if (AlwaysFail.Contains(file.RelativePath)) throw new IOException("archivo ilegible");

        if (TransientFailuresPerFile > 0)
        {
            _failuresSoFar.TryGetValue(file.RelativePath, out var so);
            if (so < TransientFailuresPerFile)
            {
                _failuresSoFar[file.RelativePath] = so + 1;
                await destination.WriteAsync(new byte[10], ct);
                throw new IOException("fallo transitorio");
            }
        }

        if (HangOn != null && string.Equals(HangOn, file.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            await destination.WriteAsync(new byte[Math.Max(1, (int)(file.Size / 2))], ct);
            await Task.Delay(Timeout.Infinite, ct);
        }

        await _inner.CopyToAsync(file, destination, ct);
        _provider.Completed(file);
    }

    public bool IsAlive() => !Dead && _inner.IsAlive();
    public void Dispose() => _inner.Dispose();
}
