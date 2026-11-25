using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Backends;

/// <summary>
/// Trata una carpeta normal del PC como si fuera un iPhone. Sirve para probar la aplicación entera
/// (reanudación, verificación) sin tener un dispositivo conectado, y para volcar un DCIM ya copiado.
/// Los parámetros opcionales permiten simular varias técnicas a la vez en las pruebas.
/// </summary>
public sealed class LocalFolderProvider : IDeviceBackendProvider
{
    private readonly string _root;
    private readonly string _name;

    public LocalFolderProvider(string rootFolder, string? displayName = null, string technique = "Carpeta local (pruebas)",
        string key = "folder", int priority = 100, bool isSnapshot = false)
    {
        _root = Path.GetFullPath(rootFolder);
        _name = displayName ?? $"Carpeta: {Path.GetFileName(_root.TrimEnd('\\'))}";
        Technique = technique;
        Key = key;
        Priority = priority;
        IsSnapshot = isSnapshot;
    }

    public string Technique { get; }
    public string Key { get; }
    public int Priority { get; }
    public bool IsSnapshot { get; }

    public IReadOnlyList<DeviceDescriptor> Discover()
    {
        if (!Directory.Exists(_root)) return Array.Empty<DeviceDescriptor>();
        return new[] { new DeviceDescriptor(_root, _name, "Local", "Carpeta", _root, Technique, IsSnapshot) };
    }

    public IDeviceBackend Open(DeviceDescriptor device)
    {
        if (!Directory.Exists(_root)) throw new DeviceLostException($"La carpeta '{_root}' ya no existe.");
        return new LocalFolderBackend(_root, device);
    }
}

public sealed class LocalFolderBackend : IDeviceBackend
{
    private readonly string _root;

    public LocalFolderBackend(string root, DeviceDescriptor device)
    {
        _root = root;
        Device = device;
    }

    public DeviceDescriptor Device { get; }
    public BackendCapabilities Capabilities => new(true, true, true, true);

    public IEnumerable<RemoteFile> EnumerateMedia(CancellationToken ct)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(_root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var rel = Path.GetRelativePath(_root, path);
            yield return new RemoteFile(path, rel, info.Length, info.LastWriteTime);
        }
    }

    public async Task CopyToAsync(RemoteFile file, Stream destination, CancellationToken ct)
    {
        await using var src = new FileStream(file.RemotePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await src.CopyToAsync(destination, 1 << 16, ct).ConfigureAwait(false);
    }

    public bool IsAlive() => Directory.Exists(_root);

    public void Dispose() { }
}
