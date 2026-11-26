using IPhonePhotos.Core.Connection;
using System.Collections.ObjectModel;
using System.Diagnostics;
using IPhonePhotos.Core.Models;
using iMobileDevice;
using iMobileDevice.Afc;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;
using iMobileDevice.Plist;

namespace IPhonePhotos.Core.Backends;

/// <summary>
/// Técnica AFC: habla directamente con el servicio de archivos del iPhone (com.apple.afc) a través de
/// libimobiledevice, sin pasar por el Explorador ni por el controlador de cámara de Windows.
/// Suele ser más rápida y funcionar cuando WPD falla. Necesita el servicio de Apple (Apple Mobile Device Service)
/// y que el iPhone esté desbloqueado y haya pulsado «Confiar».
///
/// Técnica AVANZADA y no verificada con versiones recientes de iOS (libimobiledevice de 2021): se mantiene como
/// alternativa opcional, pero la selección automática prefiere WPD (prioridad más baja). Solo lectura.
/// </summary>
public sealed class AfcProvider : IDeviceBackendProvider
{
    private static readonly object Gate = new();
    private static bool? _loaded;
    private static string? _loadError;

    public string Technique => "AFC (avanzada, no verificada con iOS recientes)";
    public string Key => "afc";
    public int Priority => 10;
    public bool IsSnapshot => false;

    /// <summary>Motivo por el que no se pudieron cargar las bibliotecas nativas (null si cargaron bien o aún no se intentó).</summary>
    public static string? LoadError => _loadError;

    /// <summary>Carga las bibliotecas nativas de libimobiledevice (una sola vez). Devuelve false si no están.</summary>
    public static bool EnsureNativeLibraries(out string? error)
    {
        lock (Gate)
        {
            if (_loaded.HasValue) { error = _loadError; return _loaded.Value; }
            try
            {
                var dir = NativeVault.Prepare(SourceDirectories(), NativeVault.DefaultTarget, out var vaultError)
                          ?? throw new DllNotFoundException(vaultError);
                // Segunda comprobación justo antes de cargar (acorta la ventana entre verificar y cargar).
                if (!NativeVault.Verify(Path.Combine(dir, "imobiledevice.dll")))
                    throw new DllNotFoundException("imobiledevice.dll cambió tras verificarla; no se carga.");
                NativeLibraries.Load(dir);
                if (!LibiMobileDevice.Instance.LibraryFound)
                    throw new DllNotFoundException($"libimobiledevice no se pudo cargar desde {dir}.");
                _loaded = true;
                _loadError = null;
            }
            catch (Exception ex)
            {
                _loaded = false;
                _loadError = ex.Message;
            }
            error = _loadError;
            return _loaded.Value;
        }
    }

    /// <summary>
    /// Dónde pueden venir las DLL: junto al .exe, en runtimes\win-x64\native o, en la app de un solo archivo, en la
    /// carpeta exacta donde .NET las extrajo (NATIVE_DLL_SEARCH_DIRECTORIES), sin rastrear %TEMP%\.net. Lo que salga de
    /// aquí solo se usa si su SHA-256 es el conocido (ver <see cref="NativeVault"/>).
    /// </summary>
    private static IEnumerable<string> SourceDirectories()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        foreach (var baseDir in new[] { AppContext.BaseDirectory, exeDir })
        {
            if (string.IsNullOrEmpty(baseDir)) continue;
            yield return baseDir;
            yield return Path.Combine(baseDir, "runtimes", "win-x64", "native");
        }
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs)
            foreach (var d in dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return d;
    }

    private static readonly object CacheGate = new();
    private static DateTime _cacheTime = DateTime.MinValue;
    private static IReadOnlyList<DeviceDescriptor> _cache = Array.Empty<DeviceDescriptor>();

    /// <summary>
    /// ¿Responde el servicio de Apple (usbmuxd, en 127.0.0.1:27015)? Sin él, libimobiledevice tarda unos 5 segundos
    /// en rendirse en cada consulta; esta comprobación tarda milisegundos y evita que la interfaz se quede esperando.
    /// </summary>
    internal static bool UsbmuxdReachable() => AppleDriverCheck.UsbmuxdReachable();

    public IReadOnlyList<DeviceDescriptor> Discover()
    {
        // La interfaz y la sesión consultan varias veces seguidas: se reutiliza el resultado durante un instante.
        lock (CacheGate)
        {
            if (DateTime.UtcNow - _cacheTime < TimeSpan.FromMilliseconds(1500)) return _cache;
            _cache = DiscoverCore();
            _cacheTime = DateTime.UtcNow;
            return _cache;
        }
    }

    private IReadOnlyList<DeviceDescriptor> DiscoverCore()
    {
        if (!EnsureNativeLibraries(out _)) return Array.Empty<DeviceDescriptor>();
        if (!UsbmuxdReachable()) return Array.Empty<DeviceDescriptor>();
        try
        {
            var count = 0;
            var err = LibiMobileDevice.Instance.iDevice.idevice_get_device_list(out ReadOnlyCollection<string> udids, ref count);
            if (err != iDeviceError.Success || udids == null) return Array.Empty<DeviceDescriptor>();
            return udids
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => new DeviceDescriptor(u, $"iPhone/iPad ({(u.Length > 8 ? u[..8] : u)}…)", "Apple", "", u, Technique))
                .ToList();
        }
        catch
        {
            return Array.Empty<DeviceDescriptor>();
        }
    }

    public IDeviceBackend Open(DeviceDescriptor device)
    {
        if (!EnsureNativeLibraries(out var error))
            throw new InvalidOperationException($"No se pudieron cargar las bibliotecas de libimobiledevice: {error}");
        if (!UsbmuxdReachable())
            throw new ConnectionProblemException(ConnectionProblem.DriverMissing,
                "El servicio de Apple (Apple Mobile Device Service) no responde. Instala o repara «Apple Devices» (Microsoft Store).");
        return AfcBackend.Connect(device);
    }
}

public sealed class AfcBackend : IDeviceBackend
{
    private readonly iDeviceHandle _device;
    private readonly LockdownClientHandle _lockdown;
    private readonly AfcClientHandle _afc;

    private AfcBackend(DeviceDescriptor descriptor, iDeviceHandle device, LockdownClientHandle lockdown, AfcClientHandle afc)
    {
        Device = descriptor;
        _device = device;
        _lockdown = lockdown;
        _afc = afc;
    }

    public DeviceDescriptor Device { get; }
    public BackendCapabilities Capabilities => new(true, true, true, true);

    internal static AfcBackend Connect(DeviceDescriptor descriptor)
    {
        var lib = LibiMobileDevice.Instance;
        iDeviceHandle? dev = null;
        LockdownClientHandle? lockdown = null;
        LockdownServiceDescriptorHandle? service = null;
        AfcClientHandle? afc = null;
        try
        {
            var derr = lib.iDevice.idevice_new(out dev, descriptor.Id);
            if (derr != iDeviceError.Success || dev == null || dev.IsInvalid)
                throw new ConnectionProblemException(ConnectionProblem.NotVisible, $"El servicio de Apple no encuentra el dispositivo ({derr}). ¿Está conectado y Apple Mobile Device Service activo?");

            var lerr = lib.Lockdown.lockdownd_client_new_with_handshake(dev, out lockdown, "IPhonePhotos");
            if (lerr != LockdownError.Success || lockdown == null || lockdown.IsInvalid)
                throw new ConnectionProblemException(ProblemOf(lerr), Explain(lerr));

            var name = ReadName(lib, lockdown);

            lerr = lib.Lockdown.lockdownd_start_service(lockdown, "com.apple.afc", out service);
            if (lerr != LockdownError.Success || service == null || service.IsInvalid)
                throw new ConnectionProblemException(ConnectionProblem.Locked, $"El iPhone no abre el servicio de archivos ({lerr}). Desbloquéalo y vuelve a intentarlo.");

            var aerr = lib.Afc.afc_client_new(dev, service, out afc);
            if (aerr != AfcError.Success || afc == null || afc.IsInvalid)
                throw new InvalidOperationException($"No se pudo abrir la conexión de archivos con el iPhone ({aerr}).");

            service.Dispose();
            var refined = descriptor with { Name = string.IsNullOrWhiteSpace(name) ? descriptor.Name : name };
            return new AfcBackend(refined, dev, lockdown, afc);
        }
        catch
        {
            afc?.Dispose();
            service?.Dispose();
            lockdown?.Dispose();
            dev?.Dispose();
            throw;
        }
    }

    private static string ReadName(ILibiMobileDevice lib, LockdownClientHandle lockdown)
    {
        try
        {
            var err = lib.Lockdown.lockdownd_get_value(lockdown, null, "DeviceName", out PlistHandle value);
            if (err != LockdownError.Success || value == null || value.IsInvalid) return "";
            lib.Plist.plist_get_string_val(value, out string name);
            value.Dispose();
            return name ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static ConnectionProblem ProblemOf(LockdownError e) => e switch
    {
        LockdownError.PasswordProtected => ConnectionProblem.Locked,
        LockdownError.UserDeniedPairing => ConnectionProblem.TrustDenied,
        LockdownError.PairingDialogResponsePending or LockdownError.InvalidPairRecord or LockdownError.MissingPairRecord
            or LockdownError.PairingFailed => ConnectionProblem.TrustPending,
        LockdownError.MuxError => ConnectionProblem.DriverMissing,
        _ => ConnectionProblem.Unknown
    };

    private static string Explain(LockdownError e) => e switch
    {
        LockdownError.PasswordProtected =>
            "El iPhone está bloqueado: desbloquéalo e inténtalo de nuevo.",
        LockdownError.UserDeniedPairing =>
            "Pulsaste «No confiar» en el iPhone. Desconecta el cable, vuelve a conectarlo y pulsa «Confiar».",
        LockdownError.PairingDialogResponsePending =>
            "Falta aceptar «Confiar en este equipo» en la pantalla del iPhone.",
        LockdownError.InvalidPairRecord or LockdownError.MissingPairRecord or LockdownError.PairingFailed =>
            "El iPhone no confía en este PC todavía. Desbloquéalo, pulsa «Confiar» e inténtalo de nuevo.",
        LockdownError.SslError =>
            "Fallo en la conexión segura con el iPhone. Reconecta el cable e inténtalo de nuevo.",
        LockdownError.MuxError =>
            "No hay comunicación con el servicio de Apple (Apple Mobile Device Service). Revisa que Apple Devices esté instalado.",
        _ => $"No se pudo iniciar la comunicación con el iPhone ({e})."
    };

    // ------------------------------------------------------------------ Archivos

    public IEnumerable<RemoteFile> EnumerateMedia(CancellationToken ct) => Walk("/DCIM", "DCIM", ct);

    private IEnumerable<RemoteFile> Walk(string dir, string relPrefix, CancellationToken ct)
    {
        foreach (var name in ReadDirectory(dir).OrderBy(n => n, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var full = dir + "/" + name;
            var info = GetInfo(full);
            if (info == null) continue;

            var kind = info.GetValueOrDefault("st_ifmt");
            if (kind == "S_IFDIR")
            {
                foreach (var f in Walk(full, relPrefix + "\\" + name, ct)) yield return f;
            }
            else if (kind == "S_IFREG")
            {
                long.TryParse(info.GetValueOrDefault("st_size"), out var size);
                DateTime? modified = null;
                if (long.TryParse(info.GetValueOrDefault("st_mtime"), out var ns) && ns > 0)
                    modified = DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000).LocalDateTime;
                yield return new RemoteFile(full, relPrefix + "\\" + name, size, modified);
            }
        }
    }

    private List<string> ReadDirectory(string path)
    {
        var err = LibiMobileDevice.Instance.Afc.afc_read_directory(_afc, path, out ReadOnlyCollection<string> list);
        if (err == AfcError.ObjectNotFound) return new List<string>(); // p. ej. aún no hay carpeta DCIM
        if (err != AfcError.Success) throw new IOException($"No se pudo leer la carpeta {path} del iPhone ({err}).");
        return (list ?? new ReadOnlyCollection<string>(new List<string>()))
            .Where(n => !string.IsNullOrEmpty(n) && n != "." && n != "..")
            .ToList();
    }

    private Dictionary<string, string>? GetInfo(string path)
    {
        var err = LibiMobileDevice.Instance.Afc.afc_get_file_info(_afc, path, out ReadOnlyCollection<string> info);
        if (err != AfcError.Success || info == null) return null;
        var dict = new Dictionary<string, string>();
        for (var i = 0; i + 1 < info.Count; i += 2) dict[info[i]] = info[i + 1];
        return dict;
    }

    public Task CopyToAsync(RemoteFile file, Stream destination, CancellationToken ct) => Task.Run(() =>
    {
        var afc = LibiMobileDevice.Instance.Afc;
        ulong handle = 0;
        var err = afc.afc_file_open(_afc, file.RemotePath, AfcFileMode.FopenRdonly, ref handle);
        if (err != AfcError.Success) throw new IOException($"No se pudo abrir {file.RelativePath} en el iPhone ({err}).");

        try
        {
            var buffer = new byte[256 * 1024];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                uint read = 0;
                err = afc.afc_file_read(_afc, handle, buffer, (uint)buffer.Length, ref read);
                if (err != AfcError.Success) throw new IOException($"Error leyendo {file.RelativePath} del iPhone ({err}).");
                if (read == 0) break;
                destination.Write(buffer, 0, (int)read);
            }
        }
        finally
        {
            afc.afc_file_close(_afc, handle);
        }
    }, ct);

    public bool IsAlive()
    {
        if (!AfcProvider.UsbmuxdReachable()) return false;
        try
        {
            var count = 0;
            var err = LibiMobileDevice.Instance.iDevice.idevice_get_device_list(out ReadOnlyCollection<string> udids, ref count);
            return err == iDeviceError.Success && udids != null &&
                   udids.Any(u => string.Equals(u, Device.Id, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _afc.Dispose(); } catch { }
        try { _lockdown.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
