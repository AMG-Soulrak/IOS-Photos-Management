using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Plist;
using Microsoft.Data.Sqlite;

namespace IPhonePhotos.Core.Backends;

/// <summary>
/// Lee las fotos del carrete de una copia de seguridad local que Apple Devices o iTunes ya hicieron del iPhone.
/// Es la red de seguridad cuando ni WPD ni AFC consiguen leer el dispositivo: haz una copia con Apple Devices y
/// extráela desde aquí. Es una FOTO FIJA del momento de la copia: las fotos posteriores no están, y las copias
/// cifradas no se pueden leer. Si "Fotos de iCloud" está activo, el carrete puede no estar en la copia.
/// Solo lectura: nunca borra nada.
/// </summary>
public sealed class BackupProvider : IDeviceBackendProvider
{
    private readonly List<string> _roots;
    private readonly Dictionary<string, (DateTime Stamp, DeviceDescriptor Device)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BackupProvider(IEnumerable<string>? extraRoots = null, bool includeDefaultRoots = true)
    {
        _roots = new List<string>();
        if (includeDefaultRoots) _roots.AddRange(DefaultRoots());
        if (extraRoots != null) _roots.AddRange(extraRoots);
    }

    public string Technique => "Copia de seguridad de Apple Devices/iTunes (solo lectura)";
    public string Key => "backup";
    public int Priority => 0;
    public bool IsSnapshot => true;

    /// <summary>Carpetas donde Apple Devices / iTunes guardan las copias de seguridad en Windows.</summary>
    public static IEnumerable<string> DefaultRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(appData)) yield return Path.Combine(appData, "Apple Computer", "MobileSync", "Backup");
        if (!string.IsNullOrEmpty(profile)) yield return Path.Combine(profile, "Apple", "MobileSync", "Backup");
    }

    public IReadOnlyList<DeviceDescriptor> Discover()
    {
        var list = new List<DeviceDescriptor>();
        foreach (var root in _roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var d = Describe(dir);
                    if (d != null) list.Add(d);
                }
            }
            catch
            {
                // carpeta ilegible o sin permisos: se ignora
            }
        }
        return list;
    }

    private DeviceDescriptor? Describe(string dir)
    {
        var manifestDb = Path.Combine(dir, "Manifest.db");
        var info = Path.Combine(dir, "Info.plist");
        if (!File.Exists(manifestDb) || !File.Exists(info)) return null;

        var stamp = File.GetLastWriteTimeUtc(info);
        if (_cache.TryGetValue(dir, out var cached) && cached.Stamp == stamp) return cached.Device;

        try
        {
            var infoDict = PlistReader.ParseFile(info) as Dictionary<string, object?>;
            var name = PlistReader.GetString(infoDict, "Device Name") ?? PlistReader.GetString(infoDict, "Display Name") ?? "iPhone";
            var udid = PlistReader.GetString(infoDict, "Unique Identifier") ?? PlistReader.GetString(infoDict, "Target Identifier")
                       ?? Path.GetFileName(dir);
            var product = PlistReader.GetString(infoDict, "Product Type") ?? "";
            var date = PlistReader.GetDate(infoDict, "Last Backup Date");

            var encrypted = false;
            var manifestPlist = Path.Combine(dir, "Manifest.plist");
            if (File.Exists(manifestPlist))
                encrypted = PlistReader.GetBool(PlistReader.ParseFile(manifestPlist) as Dictionary<string, object?>, "IsEncrypted");

            var detail = date is { } dt ? $"copia del {dt.ToLocalTime():dd/MM/yyyy HH:mm}" : "copia de seguridad";
            if (encrypted) detail += ", CIFRADA (no se puede leer)";

            var device = new DeviceDescriptor(dir, $"{name} — {detail}", "Apple", product, udid, Technique, IsSnapshot: true, Detail: detail);
            _cache[dir] = (stamp, device);
            return device;
        }
        catch
        {
            return null; // copia corrupta o con formato desconocido
        }
    }

    public IDeviceBackend Open(DeviceDescriptor device)
    {
        if (!Directory.Exists(device.Id)) throw new DeviceLostException("La copia de seguridad ya no existe.");

        var manifestPlist = Path.Combine(device.Id, "Manifest.plist");
        if (File.Exists(manifestPlist) &&
            PlistReader.GetBool(PlistReader.ParseFile(manifestPlist) as Dictionary<string, object?>, "IsEncrypted"))
            throw new InvalidOperationException(
                "La copia de seguridad está cifrada y no se puede leer. Haz una copia sin cifrar con Apple Devices.");

        return new BackupBackend(device);
    }
}

public sealed class BackupBackend : IDeviceBackend
{
    private readonly string _folder;
    private readonly SqliteConnection _db;

    internal BackupBackend(DeviceDescriptor device)
    {
        Device = device;
        _folder = device.Id;
        var dbPath = Path.Combine(_folder, "Manifest.db");
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        _db.Open();
        // Manifest.db viene de una copia que puede no ser de fiar: sin funciones de esquema y «Files» tiene que ser una tabla real
        // (una vista podría inventar filas o ejecutar funciones al leerse).
        using (var pragma = _db.CreateCommand()) { pragma.CommandText = "PRAGMA trusted_schema = OFF"; pragma.ExecuteNonQuery(); }
        using (var check = _db.CreateCommand())
        {
            check.CommandText = "SELECT type FROM sqlite_master WHERE name = 'Files'";
            if (!string.Equals(check.ExecuteScalar() as string, "table", StringComparison.Ordinal))
            {
                _db.Dispose();
                throw new InvalidDataException("La copia de seguridad no es válida: Manifest.db no tiene la tabla «Files».");
            }
        }
    }

    public DeviceDescriptor Device { get; }
    public BackendCapabilities Capabilities => new(true, true, true, true);

    private string StoredPath(string fileId) => Path.Combine(_folder, fileId[..2], fileId);

    public IEnumerable<RemoteFile> EnumerateMedia(CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "SELECT fileID, relativePath, file FROM Files " +
            "WHERE domain = 'CameraRollDomain' AND flags = 1 AND relativePath LIKE 'Media/DCIM/%' " +
            "ORDER BY relativePath";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var fileId = reader.GetString(0);
            var relative = reader.GetString(1);
            // fileID es un SHA-1 en hexadecimal; cualquier otra cosa (p. ej. "..\..\x") se ignora para no leer fuera de la copia.
            if (fileId.Length != 40 || !fileId.All(Uri.IsHexDigit)) continue;

            var stored = StoredPath(fileId);
            if (!File.Exists(stored)) continue; // la copia no incluye el contenido de este archivo

            long size = 0;
            DateTime? modified = null;
            try
            {
                var blob = reader.IsDBNull(2) ? null : (byte[])reader[2];
                if (blob != null)
                {
                    var root = PlistReader.KeyedArchiveRoot(PlistReader.Parse(blob));
                    size = PlistReader.GetLong(root, "Size") ?? 0;
                    if (PlistReader.GetLong(root, "LastModified") is { } secs)
                        modified = DateTimeOffset.FromUnixTimeSeconds(secs).LocalDateTime;
                }
            }
            catch
            {
                // metadatos ilegibles: se usa lo que diga el disco
            }
            if (size <= 0) size = new FileInfo(stored).Length;

            // Misma ruta relativa que en el iPhone: DCIM\100APPLE\IMG_0001.HEIC
            var rel = relative.StartsWith("Media/", StringComparison.OrdinalIgnoreCase) ? relative[6..] : relative;
            rel = rel.Replace('/', '\\');
            yield return new RemoteFile(stored, rel, size, modified);
        }
    }

    public async Task CopyToAsync(RemoteFile file, Stream destination, CancellationToken ct)
    {
        await using var src = new FileStream(file.RemotePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await src.CopyToAsync(destination, 1 << 16, ct).ConfigureAwait(false);
    }

    public bool IsAlive() => Directory.Exists(_folder);

    public void Dispose() => _db.Dispose();
}
