using System.Security.Cryptography;
using System.Text;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Plist;
using IPhonePhotos.Core.Transfer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>
/// Técnica de copia de seguridad (con una copia falsa creada por Python, ver Fixtures), lector de plist,
/// reconocimiento del mismo iPhone entre técnicas y carga de las bibliotecas nativas de AFC.
/// </summary>
public class TechniqueTests
{
    private const string Udid = "00000000-0000000000000000";
    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "backups");

    private static BackupProvider Provider() => new(new[] { FixtureRoot }, includeDefaultRoots: false);

    private static string StoredFile(string relativePath)
    {
        var fid = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("CameraRollDomain-" + relativePath))).ToLowerInvariant();
        return Path.Combine(FixtureRoot, Udid, fid[..2], fid);
    }

    // ------------------------------------------------------------------ Copia de seguridad

    [Fact]
    public void Backup_provider_finds_the_backups_and_flags_them_as_snapshots()
    {
        var list = Provider().Discover();
        Assert.Equal(2, list.Count);

        var phone = list.Single(d => d.SerialKey == DeviceDescriptor.NormalizeSerial(Udid));
        Assert.True(phone.IsSnapshot);
        Assert.Contains("iPhone de Pruebas", phone.Name);
        Assert.Contains("copia del", phone.Detail);
        Assert.Equal("iPhone14,5", phone.Model);
    }

    [Fact]
    public void Encrypted_backup_is_listed_but_refuses_to_open_with_a_clear_message()
    {
        var p = Provider();
        var ipad = p.Discover().Single(d => d.Name.StartsWith("iPad"));
        Assert.Contains("CIFRADA", ipad.Name);

        var ex = Assert.Throws<InvalidOperationException>(() => p.Open(ipad));
        Assert.Contains("cifrada", ex.Message);
    }

    /// <summary>Barandilla (ADR-0001): el contrato con el dispositivo solo sabe leer. Si alguien añade borrar o escribir, falla.</summary>
    [Fact]
    public void Device_contract_is_read_only()
    {
        var members = typeof(IDeviceBackend).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "CopyToAsync", "EnumerateMedia", "get_Capabilities", "get_Device", "IsAlive" }, members);
        Assert.Equal(new[] { "Discover", "get_IsSnapshot", "get_Key", "get_Priority", "get_Technique", "Open" },
            typeof(IDeviceBackendProvider).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Backup_lists_only_camera_roll_photos_with_the_same_paths_as_the_iphone()
    {
        var p = Provider();
        var phone = p.Discover().Single(d => d.Name.StartsWith("iPhone"));
        using var backend = p.Open(phone);

        var files = backend.EnumerateMedia(default).OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();

        // Quedan fuera: miniaturas, archivos de otros dominios, directorios y la foto cuyo contenido no está en la copia.
        Assert.Equal(new[]
        {
            "DCIM\\100APPLE\\IMG_0001.JPG",
            "DCIM\\100APPLE\\IMG_0002.HEIC",
            "DCIM\\100APPLE\\IMG_0003.MOV",
            "DCIM\\101APPLE\\IMG_1001.JPG"
        }, files.Select(f => f.RelativePath));
        Assert.Equal(new long[] { 3000, 5120, 7777, 4096 }, files.Select(f => f.Size));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1731234567).LocalDateTime, files[0].Modified);
    }

    [Fact]
    public async Task Whole_pipeline_copies_from_a_backup_with_identical_content_and_a_warning()
    {
        using var sb = new Sandbox();
        var log = new ActivityLog();
        var selector = new BackendSelector(new IDeviceBackendProvider[] { Provider() }, log);
        var phone = selector.DiscoverAll().Single(d => d.Name.StartsWith("iPhone"));

        var summary = await new TransferSession(selector, log).RunAsync(phone,
            new TransferOptions { DestinationRoot = sb.Dest, Retries = 1, RetryDelay = TimeSpan.FromMilliseconds(5) },
            null, null, default);

        Assert.Equal(4, summary.Copied);
        Assert.Equal(0, summary.Failed);
        Assert.NotNull(summary.SnapshotWarning);
        foreach (var rel in new[]
                 {
                     "DCIM\\100APPLE\\IMG_0001.JPG", "DCIM\\100APPLE\\IMG_0002.HEIC",
                     "DCIM\\100APPLE\\IMG_0003.MOV", "DCIM\\101APPLE\\IMG_1001.JPG"
                 })
        {
            var stored = StoredFile(rel.Replace('\\', '/').Insert(0, "Media/"));
            Assert.True(File.Exists(sb.DestFile(rel)), rel);
            Assert.Equal(Sandbox.Sha(stored), Sandbox.Sha(sb.DestFile(rel)));
        }
    }

    // ------------------------------------------------------------------ Plist

    [Fact]
    public void Plist_reads_xml_info_file()
    {
        var info = PlistReader.ParseFile(Path.Combine(FixtureRoot, Udid, "Info.plist")) as Dictionary<string, object?>;
        Assert.NotNull(info);
        Assert.Equal("iPhone de Pruebas", PlistReader.GetString(info, "Device Name"));
        Assert.Equal(Udid, PlistReader.GetString(info, "Unique Identifier"));
        Assert.Equal(new DateTime(2026, 11, 10, 9, 30, 0, DateTimeKind.Utc), PlistReader.GetDate(info, "Last Backup Date"));
    }

    [Fact]
    public void Plist_reads_binary_manifest()
    {
        var m = PlistReader.ParseFile(Path.Combine(FixtureRoot, Udid, "Manifest.plist")) as Dictionary<string, object?>;
        Assert.False(PlistReader.GetBool(m, "IsEncrypted"));
        Assert.Equal("10.0", PlistReader.GetString(m, "Version"));
        Assert.True(PlistReader.GetBool(m, "WasPasscodeSet"));

        var enc = PlistReader.ParseFile(Path.Combine(FixtureRoot, "00008101-000A1B2C3D4E5F60", "Manifest.plist")) as Dictionary<string, object?>;
        Assert.True(PlistReader.GetBool(enc, "IsEncrypted"));
    }

    [Fact]
    public void Plist_resolves_the_keyed_archive_of_a_file_record()
    {
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(FixtureRoot, Udid, "Manifest.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT file FROM Files WHERE relativePath = 'Media/DCIM/100APPLE/IMG_0001.JPG'";
        var blob = (byte[])cmd.ExecuteScalar()!;

        var root = PlistReader.KeyedArchiveRoot(PlistReader.Parse(blob));
        Assert.NotNull(root);
        Assert.Equal(3000, PlistReader.GetLong(root, "Size"));
        Assert.Equal(1731234567, PlistReader.GetLong(root, "LastModified"));
        Assert.Equal(33188, PlistReader.GetLong(root, "Mode"));
    }

    [Fact]
    public void Plist_rejects_garbage_without_hanging()
    {
        Assert.ThrowsAny<Exception>(() => PlistReader.Parse(Encoding.ASCII.GetBytes("bplist00" + new string('x', 50))));
        Assert.ThrowsAny<Exception>(() => PlistReader.Parse(new byte[] { 1, 2, 3 }));
    }

    // ------------------------------------------------------------------ Mismo iPhone, varias técnicas

    [Fact]
    public void Wpd_device_id_yields_the_same_serial_as_the_afc_udid()
    {
        var wpdSerial = WpdProvider.SerialFromDeviceId(
            @"\\?\usb#vid_05ac&pid_12a8#000000000000000000000000#{6ac27878-a6fa-4155-ba85-f98f491d4f33}");
        Assert.Equal("000000000000000000000000", wpdSerial);

        var wpd = new DeviceDescriptor("wpd-id", "Apple iPhone", "Apple", "", wpdSerial, "WPD");
        var afc = new DeviceDescriptor(Udid, "iPhone", "Apple", "", Udid, "AFC");
        var backup = Provider().Discover().Single(d => d.Name.StartsWith("iPhone"));
        Assert.True(wpd.IsSameDevice(afc));
        Assert.True(afc.IsSameDevice(backup));
        Assert.True(wpd.IsSameDevice(backup));

        var other = new DeviceDescriptor("x", "Apple iPhone", "Apple", "", "00008101000A1B2C3D4E5F60", "WPD");
        Assert.False(wpd.IsSameDevice(other));
    }

    [Fact]
    public void Old_style_40_hex_udid_is_also_recognised()
    {
        var wpdSerial = WpdProvider.SerialFromDeviceId(
            @"\\?\usb#vid_05ac&pid_12a0#a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2#{6ac27878-a6fa-4155-ba85-f98f491d4f33}");
        Assert.Equal("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2", wpdSerial);
        Assert.Equal("", WpdProvider.SerialFromDeviceId(@"\\?\usb#vid_1234&pid_0001#abc#{x}")); // no es de Apple
    }

    // ------------------------------------------------------------------ AFC

    [Fact]
    public void Afc_native_libraries_load_and_discovery_is_safe_without_an_iphone()
    {
        var ok = AfcProvider.EnsureNativeLibraries(out var error);
        Assert.True(ok, "No cargan las bibliotecas de libimobiledevice: " + error);

        var provider = new AfcProvider();
        var devices = provider.Discover(); // sin iPhone (o sin servicio de Apple): lista vacía, nunca una excepción
        Assert.NotNull(devices);
        Assert.True(provider.Priority < new WpdProvider().Priority, "la selección automática debe preferir WPD a AFC");
    }

    [Fact]
    public void Afc_open_of_a_missing_device_fails_with_a_message_instead_of_crashing()
    {
        var provider = new AfcProvider();
        var ex = Assert.ThrowsAny<Exception>(() =>
            provider.Open(new DeviceDescriptor("00000000-0000000000000000", "x", "", "", "x", provider.Technique)));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}
