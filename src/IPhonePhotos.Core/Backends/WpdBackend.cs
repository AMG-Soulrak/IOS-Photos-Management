using IPhonePhotos.Core.Models;
using MediaDevices;

namespace IPhonePhotos.Core.Backends;

/// <summary>
/// Técnica WPD/MTP: Windows ve el iPhone/iPad como un dispositivo portátil (igual que el Explorador).
/// Se apoya en la librería MediaDevices (MIT). Necesita el controlador de Apple instalado, pero no
/// necesita túneles ni permisos de administrador, y funciona con todas las versiones de iOS.
/// </summary>
public sealed class WpdProvider : IDeviceBackendProvider
{
    /// <summary>Si es true, lista también dispositivos que no son de Apple (Android, cámaras…).</summary>
    public bool IncludeNonApple { get; init; }

    public string Technique => "WPD/MTP (Windows Portable Devices)";
    public string Key => "wpd";
    public int Priority => 50;
    public bool IsSnapshot => false;

    /// <summary>
    /// Los iPhone/iPad usan como número de serie USB su UDID, que Windows incluye en el identificador del dispositivo
    /// (p. ej. \\?\usb#vid_05ac&amp;pid_12a8#000000000000000000000000#{...}). Sacarlo de ahí permite reconocer el mismo
    /// aparato en otras técnicas sin conectarse. Es una heurística: si el formato no encaja, el número queda vacío.
    /// </summary>
    public static string SerialFromDeviceId(string deviceId)
    {
        var m = System.Text.RegularExpressions.Regex.Match(deviceId ?? "",
            @"vid_05ac&pid_[0-9a-f]{4}#([0-9a-f]{20,40})#", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    public IReadOnlyList<DeviceDescriptor> Discover()
    {
        var list = new List<DeviceDescriptor>();
        try
        {
            foreach (var d in ListDevices())
            {
                var manufacturer = Safe(() => d.Manufacturer);
                var friendly = Safe(() => d.FriendlyName);
                var description = Safe(() => d.Description);
                if (!IncludeNonApple && !LooksLikeApple(manufacturer, friendly, description)) continue;
                var name = !string.IsNullOrWhiteSpace(friendly) ? friendly : description;
                list.Add(new DeviceDescriptor(d.DeviceId, name, manufacturer, description, SerialFromDeviceId(d.DeviceId), Technique));
            }
        }
        catch
        {
            // Sin controlador o sin dispositivos, la enumeración puede fallar: se interpreta como "ninguno".
        }
        return list;
    }

    public IDeviceBackend Open(DeviceDescriptor device)
    {
        // Solo lectura, sin alternativa: "Default" en WPD puede pedir también escritura y la app ya no escribe nunca.
        var dev = ListDevices().FirstOrDefault(x => string.Equals(x.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
                  ?? throw new DeviceLostException($"No se encuentra '{device.DisplayName}'. ¿Está conectado y desbloqueado?",
                      problem: Connection.ConnectionProblem.NotVisible);
        try
        {
            dev.Connect(MediaDeviceAccess.GenericRead, MediaDeviceShare.Default, false);
        }
        catch (Exception ex)
        {
            try { if (dev.IsConnected) dev.Disconnect(); } catch { }
            try { dev.Dispose(); } catch { }
            throw new InvalidOperationException($"No se pudo abrir el dispositivo: {ex.Message}", ex);
        }

        var refined = device with
        {
            Model = Safe(() => dev.Model) is { Length: > 0 } m ? m : device.Model,
            // Se prefiere el UDID sacado del identificador USB (coincide con el de las demás técnicas);
            // la propiedad SerialNumber de WPD puede ser el número de serie de fábrica, que es otra cosa.
            SerialNumber = device.SerialNumber.Length > 0 ? device.SerialNumber : Safe(() => dev.SerialNumber)
        };
        return new WpdBackend(dev, refined, this);
    }

    private static bool LooksLikeApple(params string[] values) =>
        values.Any(v => !string.IsNullOrEmpty(v) &&
            (v.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
             v.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
             v.Contains("iPad", StringComparison.OrdinalIgnoreCase) ||
             v.Contains("iPod", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Dispositivos portátiles que Windows ve ahora mismo (lista vacía si no hay o si falla).</summary>
    internal static IReadOnlyList<MediaDevice> ListDevices()
        => MediaDeviceManager.Instance?.GetDevices()?.ToList() ?? new List<MediaDevice>();

    internal static string Safe(Func<string?> read)
    {
        try { return read() ?? ""; } catch { return ""; }
    }
}

public sealed class WpdBackend : IDeviceBackend
{
    private MediaDevice _device;
    private readonly WpdProvider _provider;

    internal WpdBackend(MediaDevice device, DeviceDescriptor descriptor, WpdProvider provider)
    {
        _device = device;
        _provider = provider;
        Device = descriptor;
    }

    public DeviceDescriptor Device { get; }

    public BackendCapabilities Capabilities => new(CanList: true, CanRead: true, ReportsDates: true, ReportsSizes: true);

    public IEnumerable<RemoteFile> EnumerateMedia(CancellationToken ct)
    {
        var storages = new List<(string Path, string Name, string? Dcim)>();
        Retry(() =>
        {
            storages.Clear();
            foreach (var storage in _device.GetRootDirectory().EnumerateDirectories())
            {
                var dcim = storage.EnumerateDirectories().FirstOrDefault(d => d.Name.Equals("DCIM", StringComparison.OrdinalIgnoreCase));
                storages.Add((storage.FullName, storage.Name, dcim?.FullName));
            }
        }, ct);
        foreach (var (path, name, dcim) in storages)
            // Versiones recientes de iOS no exponen DCIM por WPD: las fotos cuelgan del almacenamiento en carpetas por mes (202509_a…).
            foreach (var f in dcim != null ? Walk(dcim, "DCIM", ct) : Walk(path, name, ct)) yield return f;
    }

    private IEnumerable<RemoteFile> Walk(string path, string relPrefix, CancellationToken ct)
    {
        // Cada carpeta se lee entera y con reintento: así, si WPD se cae a mitad, se reconecta y se repite solo esa carpeta.
        var files = new List<RemoteFile>();
        var subs = new List<(string Path, string Name)>();
        Retry(() =>
        {
            files.Clear();
            subs.Clear();
            var dir = _device.GetDirectoryInfo(path);
            foreach (var file in dir.EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                DateTime? modified = file.LastWriteTime ?? file.CreationTime;
                files.Add(new RemoteFile(file.FullName, relPrefix + "\\" + file.Name, (long)file.Length, modified));
            }
            foreach (var sub in dir.EnumerateDirectories()) subs.Add((sub.FullName, sub.Name));
        }, ct);
        foreach (var f in files) yield return f;
        foreach (var (subPath, subName) in subs)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var f in Walk(subPath, relPrefix + "\\" + subName, ct)) yield return f;
        }
    }

    // shortcut: 3 reintentos con pausa corta; si el iPhone sigue sin responder se propaga el error (suele ser cable o iPhone bloqueado).
    private void Retry(Action read, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { read(); return; }
            catch (Exception ex) when (attempt < 4 && ex is not OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(1500 * attempt);
                Reconnect();
            }
        }
    }

    private void Reconnect()
    {
        try { if (_device.IsConnected) _device.Disconnect(); } catch { }
        try { _device.Dispose(); } catch { }
        _device = ((WpdBackend)_provider.Open(Device))._device;
    }

    public async Task CopyToAsync(RemoteFile file, Stream destination, CancellationToken ct)
    {
        try
        {
            await _device.DownloadFileAsync(file.RemotePath, destination, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // Si la librería necesita un stream con búsqueda, se descarga a un archivo temporal y se vuelca.
            var tmp = Path.Combine(Path.GetTempPath(), "iphotos-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await _device.DownloadFileAsync(file.RemotePath, tmp, ct).ConfigureAwait(false);
                await using var src = File.OpenRead(tmp);
                await src.CopyToAsync(destination, 1 << 16, ct).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    public bool IsAlive()
    {
        try
        {
            return WpdProvider.ListDevices().Any(x => string.Equals(x.DeviceId, Device.Id, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { if (_device.IsConnected) _device.Disconnect(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
