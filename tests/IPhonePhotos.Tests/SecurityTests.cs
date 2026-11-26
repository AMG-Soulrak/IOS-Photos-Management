using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Transfer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>S1: ninguna ruta que venga de fuera (copia de seguridad, dispositivo, manifiesto) puede escribir fuera del destino.</summary>
public class SecurityTests
{
    [Theory]
    [InlineData("DCIM\\100APPLE\\IMG_0001.HEIC", true)]
    [InlineData("..\\..\\x.jpg", false)]
    [InlineData("DCIM\\..\\..\\x.jpg", false)]
    [InlineData("DCIM/../../x.jpg", false)]
    [InlineData("C:\\Windows\\x.jpg", false)]
    [InlineData("\\x.jpg", false)]
    [InlineData("DCIM\\a:b.jpg", false)]
    [InlineData("", false)]
    public void SafePath_only_accepts_paths_inside_the_root(string rel, bool ok)
    {
        var root = Path.Combine(Path.GetTempPath(), "raiz-segura");
        Assert.Equal(ok, SafePath.IsInside(root, rel));
    }

    [Fact]
    public async Task Backup_with_a_dotdot_path_is_rejected_and_nothing_is_written_outside()
    {
        using var sb = new Sandbox();
        var backups = Path.Combine(sb.Root, "backups");
        CopyDir(Path.Combine(AppContext.BaseDirectory, "Fixtures", "backups"), backups);
        var provider = new BackupProvider(new[] { backups }, includeDefaultRoots: false);
        var phoneDir = provider.Discover().Single(d => d.Name.StartsWith("iPhone")).Id;

        // Fila manipulada en Manifest.db: el filtro LIKE 'Media/DCIM/%' la deja pasar.
        const string fid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Directory.CreateDirectory(Path.Combine(phoneDir, "aa"));
        File.WriteAllBytes(Path.Combine(phoneDir, "aa", fid), Sandbox.Bytes(1234, 7));
        using (var con = new SqliteConnection($"Data Source={Path.Combine(phoneDir, "Manifest.db")}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "INSERT INTO Files (fileID, domain, relativePath, flags, file) " +
                              $"SELECT '{fid}', domain, 'Media/DCIM/../../escapada.jpg', flags, file FROM Files " +
                              "WHERE relativePath = 'Media/DCIM/100APPLE/IMG_0001.JPG'";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        SqliteConnection.ClearAllPools();

        var log = new ActivityLog();
        var warnings = new List<string>();
        log.Entry += e => { if (e.Level == LogLevel.Warn) warnings.Add(e.Message); };
        var selector = new BackendSelector(new IDeviceBackendProvider[] { new BackupProvider(new[] { backups }, includeDefaultRoots: false) }, log);
        var phone = selector.DiscoverAll().Single(d => d.Name.StartsWith("iPhone"));

        var summary = await new TransferSession(selector, log).RunAsync(phone,
            new TransferOptions { DestinationRoot = sb.Dest, Retries = 1, RetryDelay = TimeSpan.FromMilliseconds(5) }, null, null, default);

        Assert.Equal(4, summary.Copied); // las 4 buenas se copian igual
        Assert.Empty(Directory.GetFiles(sb.Root, "escapada.jpg", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(sb.Dest)!, "..", "escapada.jpg")));
        Assert.Contains(warnings, w => w.Contains("Ruta rechazada"));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Tampered_manifest_path_never_writes_outside_the_destination()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(2);
        var provider = new FlakyProvider(sb.Source);
        var log = new ActivityLog();
        var selector = new BackendSelector(new[] { provider }, log);
        var device = provider.Discover().Single();
        var options = new TransferOptions { DestinationRoot = sb.Dest, Retries = 0, RetryDelay = TimeSpan.Zero };

        // Primer escaneo para crear los registros y, después, se manipula el manifiesto a mano.
        using (var backend = provider.Open(device))
        using (var store = ManifestStore.Open(sb.Dest))
        {
            new TransferEngine(log).Scan(backend, store, options, null, default);
            foreach (var r in store.All())
            {
                r.LocalRelativePath = "..\\fuera\\" + Path.GetFileName(r.RelativePath);
                store.Upsert(r);
            }
        }

        var summary = await new TransferSession(selector, log).RunAsync(device, options, null, null, default);

        Assert.Equal(2, summary.Failed);
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "fuera")));
    }

    [Fact]
    public void Connection_report_has_no_serial_device_name_or_file_names()
    {
        var device = new DeviceDescriptor("id", "iPhone de María", "Apple", "iPhone14,5", "SERIE-SECRETA-123", "WPD");
        var probe = new ProbeResult(device, "WPD", true, null, 3, "DCIM\\100APPLE\\IMG_0001.HEIC", 1234, true, 900, true, true, TimeSpan.FromSeconds(1), 100);
        var text = BackendSelector.FormatReport(new DriverStatus(true, true, "ok"), new[] { probe });

        Assert.Contains("iPhone14,5", text);
        Assert.DoesNotContain("María", text);
        Assert.DoesNotContain("SECRETA", text);
        Assert.DoesNotContain("IMG_0001", text);
    }

    [Fact]
    public void Native_vault_rejects_a_planted_dll_and_accepts_the_package_ones()
    {
        using var sb = new Sandbox();
        var planted = Path.Combine(sb.Root, "plantada");
        Directory.CreateDirectory(planted);
        foreach (var n in new[] { "imobiledevice.dll", "plist.dll", "usbmuxd.dll" })
            File.WriteAllBytes(Path.Combine(planted, n), Sandbox.Bytes(4096, 3));

        var target = Path.Combine(sb.Root, "native");
        Assert.Null(NativeVault.Prepare(new[] { planted }, target, out var error));
        Assert.Contains("imobiledevice.dll", error);
        Assert.False(File.Exists(Path.Combine(target, "imobiledevice.dll")));

        // La plantada va primero, pero se salta y se toma la buena del paquete.
        var real = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        Assert.Equal(target, NativeVault.Prepare(new[] { planted, real }, target, out _));
        Assert.True(NativeVault.Verify(Path.Combine(target, "imobiledevice.dll")));

        // Si alguien la cambia en la carpeta propia, la siguiente preparación la repone.
        File.WriteAllBytes(Path.Combine(target, "imobiledevice.dll"), Sandbox.Bytes(10, 1));
        Assert.Equal(target, NativeVault.Prepare(new[] { real }, target, out _));
        Assert.True(NativeVault.Verify(Path.Combine(target, "imobiledevice.dll")));
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
    }
}
