using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Organize;
using IPhonePhotos.Core.Plist;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>Fallos hallados en las pruebas de ejecución: cada uno queda fijado por una prueba.</summary>
[Collection("Personas")] // PeopleService.DataRoot es estático
public sealed class RegressionTests : IDisposable
{
    private readonly string _oldRoot = PeopleService.DataRoot;
    public void Dispose() => PeopleService.DataRoot = _oldRoot;

    private static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "reg-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    private static void Jpg(string path, int seed)
    {
        const int s = 160; var px = new byte[s * s * 3];
        for (var y = 0; y < s; y++) for (var x = 0; x < s; x++) { var i = (y * s + x) * 3; px[i] = (byte)(x + seed); px[i + 1] = (byte)y; px[i + 2] = (byte)(200 - x); }
        var enc = new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(s, s, 96, 96, PixelFormats.Bgr24, null, px, s * 3)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path); enc.Save(fs);
        File.SetLastWriteTime(path, new DateTime(2024, 3, 10, 12, 0, 0));
    }

    private static List<AlbumItem> Items(string root) => Album.Load(root).Items;

    [Fact]
    public void Duplicates_without_Personas_consent_writes_no_face_data()
    {
        PeopleService.DataRoot = Tmp(); PeopleService.Disable();
        var root = Tmp(); Jpg(Path.Combine(root, "a.jpg"), 0); Jpg(Path.Combine(root, "b.jpg"), 1);
        DuplicateService.Find(root, Items(root));
        Assert.False(File.Exists(Path.Combine(ManifestStore.MetaFolder(root), "faces.json")));
        Assert.False(DuplicateService.FacesAvailable);
    }

    [Fact]
    public void DeleteAll_also_removes_faces_json_of_that_library()
    {
        PeopleService.DataRoot = Tmp(); PeopleService.Enable();
        var root = Tmp(); var meta = ManifestStore.MetaFolder(root); Directory.CreateDirectory(meta);
        File.WriteAllText(Path.Combine(meta, "faces.json"), "{}");
        PeopleService.DeleteAll(root);
        Assert.False(File.Exists(Path.Combine(meta, "faces.json")));
    }

    private static byte[] PlistBomb(int levels)
    {
        var ms = new List<byte>(); ms.AddRange("bplist00"u8.ToArray());
        var offsets = new List<byte> { (byte)ms.Count }; ms.AddRange(new byte[] { 0x10, 0x01 });
        for (var k = 1; k <= levels; k++) { offsets.Add((byte)ms.Count); ms.AddRange(new byte[] { 0xA2, (byte)(k - 1), (byte)(k - 1) }); }
        var offTable = ms.Count; ms.AddRange(offsets);
        var trailer = new byte[32]; trailer[6] = 1; trailer[7] = 1;
        BitConverter.GetBytes((long)(levels + 1)).Reverse().ToArray().CopyTo(trailer, 8);
        BitConverter.GetBytes((long)levels).Reverse().ToArray().CopyTo(trailer, 16);
        BitConverter.GetBytes((long)offTable).Reverse().ToArray().CopyTo(trailer, 24);
        ms.AddRange(trailer); return ms.ToArray();
    }

    [Fact]
    public void Plist_with_shared_references_is_not_exponential()
    {
        var sw = Stopwatch.StartNew();
        PlistReader.Parse(PlistBomb(60)); // sin memoria compartida, 2^60 lecturas
        Assert.True(sw.ElapsedMilliseconds < 2000, $"tardó {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Plist_with_huge_declared_length_fails_cleanly()
    {
        var d = new List<byte>(); d.AddRange("bplist00"u8.ToArray());
        d.AddRange(new byte[] { 0xAF, 0x13, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }); // array con 2^63-1 elementos
        d.Add(8); // tabla de desplazamientos
        var trailer = new byte[32]; trailer[6] = 1; trailer[7] = 1; trailer[15] = 1; trailer[31] = 18;
        d.AddRange(trailer);
        Assert.ThrowsAny<FormatException>(() => PlistReader.Parse(d.ToArray()));
    }

    [Fact]
    public void Scanner_does_not_follow_directory_junctions()
    {
        var outside = Tmp(); Jpg(Path.Combine(outside, "fuera.jpg"), 5);
        var lib = Tmp(); Jpg(Path.Combine(lib, "dentro.jpg"), 6);
        var psi = new ProcessStartInfo("cmd", $"/c mklink /J \"{Path.Combine(lib, "enlace")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false };
        Process.Start(psi)!.WaitForExit();
        if (!Directory.Exists(Path.Combine(lib, "enlace"))) return; // sin permiso para crear el enlace: nada que comprobar
        var found = MediaScanner.Scan(lib).Select(m => m.RelativePath).ToList();
        Assert.Equal(new[] { "dentro.jpg" }, found);
    }

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("nul", "nul_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("aux.txt", "aux.txt_")]
    [InlineData("Vacaciones", "Vacaciones")]
    [InlineData("CONSOLA", "CONSOLA")]
    public void Album_names_avoid_windows_device_names(string input, string expected)
        => Assert.Equal(expected, AlbumService.Clean(input));

    [Fact]
    public void Album_name_is_capped_and_creatable()
    {
        var clean = AlbumService.Clean(new string('x', 300));
        Assert.True(clean.Length <= 100);
        var root = Tmp();
        Assert.True(Directory.Exists(Path.Combine(AlbumService.AlbumsPath(root), AlbumService.Create(root, "CON"))));
    }

    [Fact]
    public void Rename_with_open_file_gives_friendly_error_and_keeps_album()
    {
        var root = Tmp(); Jpg(Path.Combine(root, "a.jpg"), 1);
        var svc = new AlbumService(new ActivityLog()); AlbumService.Create(root, "Viaje");
        svc.Add(root, null, "Viaje", Items(root), null, CancellationToken.None);
        var file = Directory.GetFiles(Path.Combine(AlbumService.AlbumsPath(root), "Viaje")).First();
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => svc.Rename(root, null, "Viaje", "Viaje2"));
            Assert.Contains("otro programa", ex.Message);
        }
        Assert.True(Directory.Exists(Path.Combine(AlbumService.AlbumsPath(root), "Viaje")));
    }

    [Fact]
    public void Cancelling_Duplicates_keeps_the_fingerprints_computed_so_far()
    {
        var root = Tmp();
        for (var i = 0; i < 40; i++) Jpg(Path.Combine(root, $"f{i:00}.jpg"), i);
        using var cts = new CancellationTokenSource();
        var progress = new Sync(p => { if (p.Done >= 15) cts.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => DuplicateService.Find(root, Items(root), progress: progress, ct: cts.Token));
        Assert.True(File.Exists(Path.Combine(ManifestStore.MetaFolder(root), "dhash.json")));
    }

    [Fact]
    public void Backup_whose_Files_is_a_view_is_rejected()
    {
        var bk = Tmp(); var dir = Path.Combine(bk, "AAAA"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Info.plist"), "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>Device Name</key><string>Falso</string></dict></plist>");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "Manifest.db")}"))
        {
            c.Open();
            new Microsoft.Data.Sqlite.SqliteCommand("CREATE VIEW Files AS SELECT 'a' AS fileID, 'CameraRollDomain' AS domain, 'x' AS relativePath, 1 AS flags, NULL AS file", c).ExecuteNonQuery();
        }
        var prov = new IPhonePhotos.Core.Backends.BackupProvider(new[] { bk }, includeDefaultRoots: false);
        var dev = prov.Discover();
        if (dev.Count == 0) return; // la copia ni se detecta: también es válido
        Assert.Throws<InvalidDataException>(() => prov.Open(dev[0]));
    }

    private sealed class Sync : IProgress<(int Done, int Total)>
    {
        private readonly Action<(int Done, int Total)> _a; public Sync(Action<(int Done, int Total)> a) => _a = a;
        public void Report((int Done, int Total) v) => _a(v);
    }
}
