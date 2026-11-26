using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Connection;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Organize;
using IPhonePhotos.Core.Transfer;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>Copia de la v1.6: fallos que no detienen el resto, reintento selectivo, iCloud, informe, fechas, JPG y ediciones.</summary>
public class CopyV16Tests
{
    private static TransferOptions Options(Sandbox sb) => new()
    {
        DestinationRoot = sb.Dest,
        Retries = 0,
        RetryDelay = TimeSpan.FromMilliseconds(1),
        ReconnectTimeout = TimeSpan.FromMilliseconds(300),
        ReconnectPollInterval = TimeSpan.FromMilliseconds(10),
        AllowSnapshotFallback = false
    };

    private static (TransferSession Session, DeviceDescriptor Device) Make(FlakyProvider provider)
    {
        var log = new ActivityLog();
        var selector = new BackendSelector(new[] { provider }, log);
        return (new TransferSession(selector, log), selector.DiscoverAll().Single());
    }

    /// <summary>JPEG sintético, con fecha EXIF de captura si se da.</summary>
    internal static void Jpeg(string path, DateTime? taken = null, int seed = 1)
    {
        var rnd = new Random(seed);
        var px = new byte[64 * 64 * 3];
        rnd.NextBytes(px);
        var bmp = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgr24, null, px, 64 * 3);
        BitmapMetadata? meta = null;
        if (taken is { } t)
        {
            meta = new BitmapMetadata("jpg");
            meta.SetQuery("/app1/ifd/exif/{ushort=36867}", t.ToString("yyyy:MM:dd HH:mm:ss"));
        }
        var enc = new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(bmp, null, meta, null));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>MOV mínimo: ftyp + moov/mvhd (versión 0) con la fecha de creación dada.</summary>
    internal static void Mov(string path, DateTime? createdUtc)
    {
        var secs = createdUtc is { } c ? (uint)(c - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds : 0u;
        static byte[] Be(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        var mvhdBody = new List<byte> { 0, 0, 0, 0 };
        mvhdBody.AddRange(Be(secs)); mvhdBody.AddRange(Be(secs)); mvhdBody.AddRange(Be(600)); mvhdBody.AddRange(Be(0));
        mvhdBody.AddRange(new byte[80]);
        var mvhd = Be((uint)(8 + mvhdBody.Count)).Concat("mvhd"u8.ToArray()).Concat(mvhdBody).ToArray();
        var moov = Be((uint)(8 + mvhd.Length)).Concat("moov"u8.ToArray()).Concat(mvhd).ToArray();
        var ftyp = Be(16).Concat("ftypqt  "u8.ToArray()).Concat(new byte[4]).ToArray();
        var mdat = Be(8 + 32).Concat("mdat"u8.ToArray()).Concat(new byte[32]).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ftyp.Concat(mdat).Concat(moov).ToArray()); // moov al final, como en muchos vídeos
    }

    [Fact]
    public async Task Over_1200_files_one_bad_file_does_not_stop_the_rest_and_the_report_has_reasons_without_names()
    {
        using var sb = new Sandbox();
        for (var i = 0; i < 1210; i++) sb.AddPhoto($"DCIM\\{100 + i / 500}APPLE\\IMG_{i:0000}.HEIC", 40 + i % 7, seed: i + 1);
        var provider = new FlakyProvider(sb.Source);
        var bad = new[] { "DCIM\\100APPLE\\IMG_0003.HEIC", "DCIM\\102APPLE\\IMG_1200.HEIC" };
        provider.ConfigureConnection = (_, b) => { foreach (var f in bad) b.AlwaysFail.Add(f); return b; };
        var (session, device) = Make(provider);

        var s = await session.RunAsync(device, Options(sb), null, null, default);

        Assert.Equal(1208, s.Copied);
        Assert.Equal(bad.OrderBy(x => x), s.FailedFiles.OrderBy(x => x));
        Assert.All(bad, f => Assert.Equal("El iPhone no dejó leer el archivo", s.FailureReasons[f]));
        var report = File.ReadAllText(Path.Combine(sb.Dest, TransferSession.LastCopyReportName));
        Assert.Equal(s.ReportText, report);
        Assert.Contains("No copiadas: 2", report);
        Assert.Contains("El iPhone no dejó leer el archivo: 2", report);
        Assert.DoesNotContain("IMG_", report); // sin nombres de archivo en el .txt
        Assert.Contains(CopyReport.ICloudDeleteWarning, report);
        Assert.Contains(CopyReport.NeverDeletes, report);
        Assert.Contains("no se pudo comprobar", report); // el simulador, como las técnicas reales, no informa de iCloud
    }

    [Fact]
    public async Task Retry_one_not_copied_file_then_the_rest()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(6);
        var failing = true;
        var bad = new[] { "DCIM\\100APPLE\\IMG_0001.HEIC", "DCIM\\101APPLE\\IMG_0005.HEIC" };
        var provider = new FlakyProvider(sb.Source)
        {
            ConfigureConnection = (_, b) => { if (failing) foreach (var f in bad) b.AlwaysFail.Add(f); return b; }
        };
        var (session, device) = Make(provider);
        Assert.False(CopyReport.HasPreviousCopy(sb.Dest));

        var first = await session.RunAsync(device, Options(sb), null, null, default);
        Assert.Equal(2, first.Failed);
        Assert.True(CopyReport.HasPreviousCopy(sb.Dest)); // la ventana pasa a «Copiar solo lo nuevo»

        failing = false;
        var opts = Options(sb);
        opts.OnlyPaths = new HashSet<string>(new[] { bad[0] }, StringComparer.OrdinalIgnoreCase);
        var one = await session.RunAsync(device, opts, null, null, default);
        Assert.Equal(1, one.Copied);
        Assert.True(File.Exists(sb.DestFile(bad[0])));
        Assert.False(File.Exists(sb.DestFile(bad[1])));

        var rest = await session.RunAsync(device, Options(sb), null, null, default);
        Assert.Equal(1, rest.Copied);
        Assert.Equal(0, rest.Failed);
        Assert.Equal(1, provider.CompletedCopies[bad[1]]);
        Assert.All(provider.CompletedCopies.Values, n => Assert.Equal(1, n)); // nada se copia dos veces
    }

    [Fact]
    public async Task ICloud_only_photos_are_reported_only_when_the_device_says_so()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(8);
        var provider = new FlakyProvider(sb.Source) { DeclaredMediaCount = 10, OptimizeStorage = true };
        var (session, device) = Make(provider);
        var s = await session.RunAsync(device, Options(sb), null, null, default);

        Assert.Equal(2, s.OnlyInCloud);
        Assert.Contains("Puede haber 2 fotos solo en iCloud", s.ReportText);
        Assert.Contains(s.Notices, n => n.Contains("Optimizar almacenamiento"));

        provider.DeclaredMediaCount = 8;
        var again = await session.RunAsync(device, Options(sb), null, null, default);
        Assert.Equal(0, again.OnlyInCloud);
        Assert.Contains("ninguna según el dispositivo", again.ReportText);
        Assert.DoesNotContain("copia completa", again.ReportText);
    }

    [Fact]
    public async Task Cut_that_never_comes_back_is_a_disconnection_with_continue()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(5);
        var provider = new FlakyProvider(sb.Source) { ReappearAfterPolls = int.MaxValue };
        provider.ConfigureConnection = (_, b) => { b.UnplugAfterFiles = 2; return b; };
        var (session, device) = Make(provider);

        var ex = await Assert.ThrowsAsync<DeviceLostException>(() => session.RunAsync(device, Options(sb), null, null, default));
        Assert.Equal(ConnectionProblem.Disconnected, ex.Problem);
        var user = ErrorExplainer.Explain(ex);
        Assert.Equal("copia-se-corta", user.GuideRowId);
        Assert.Contains("Continuar", user.Action);
    }

    // ------------------------------------------------------------------ Fechas

    [Fact]
    public async Task Capture_date_decides_year_month_and_creation_time_while_modified_keeps_the_device_date()
    {
        using var sb = new Sandbox();
        var taken = new DateTime(2019, 5, 6, 7, 8, 9);
        var src = Path.Combine(sb.Source, "DCIM", "100APPLE", "IMG_0001.JPG");
        Jpeg(src, taken);
        var deviceDate = new DateTime(2024, 2, 3, 10, 0, 0);
        File.SetLastWriteTime(src, deviceDate);
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0002.HEIC", 300, seed: 2); // sin EXIF legible

        var (session, device) = Make(new FlakyProvider(sb.Source));
        var opts = Options(sb);
        opts.OrganizeByDate = true;
        var s = await session.RunAsync(device, opts, null, null, default);

        var copied = sb.DestFile("2019\\05\\IMG_0001.JPG");
        Assert.True(File.Exists(copied));
        Assert.Equal(taken, File.GetCreationTime(copied));
        Assert.Equal(deviceDate, File.GetLastWriteTime(copied));
        Assert.True(File.Exists(sb.DestFile("2024\\01\\IMG_0002.HEIC"))); // sin fecha de captura: la del dispositivo
        Assert.Equal(1, s.NoCaptureDate);
        Assert.Contains("Sin fecha de captura fiable: 1", s.ReportText);

        // Repetir no vuelve a descargar nada (la fecha de modificación sigue siendo la del dispositivo).
        var again = await session.RunAsync(device, opts, null, null, default);
        Assert.Equal(0, again.Copied);
    }

    [Fact]
    public async Task Video_capture_date_comes_from_quicktime_and_missing_date_is_null()
    {
        using var sb = new Sandbox();
        var created = new DateTime(2020, 8, 15, 18, 30, 0, DateTimeKind.Utc);
        Mov(Path.Combine(sb.Source, "DCIM", "100APPLE", "IMG_0010.MOV"), created);
        var noDate = Path.Combine(sb.Root, "nodate.mov");
        Mov(noDate, null);

        Assert.Equal(created.ToLocalTime(), CaptureDate.Read(Path.Combine(sb.Source, "DCIM", "100APPLE", "IMG_0010.MOV"), "IMG_0010.MOV"));
        Assert.Null(CaptureDate.Read(noDate, "x.mov"));
        Assert.Null(CaptureDate.Read(noDate, "x.aae"));

        var (session, device) = Make(new FlakyProvider(sb.Source));
        var opts = Options(sb);
        opts.OrganizeByDate = true;
        await session.RunAsync(device, opts, null, null, default);
        var local = created.ToLocalTime();
        Assert.True(File.Exists(sb.DestFile($"{local:yyyy}\\{local:MM}\\IMG_0010.MOV")));
    }

    // ------------------------------------------------------------------ HEIC → JPG

    [Fact]
    public async Task Jpg_copy_is_made_next_to_the_library_and_a_failed_conversion_never_fails_the_copy()
    {
        using var sb = new Sandbox();
        Jpeg(Path.Combine(sb.Source, "DCIM", "100APPLE", "IMG_0001.HEIC"), seed: 3); // decodificable (es un JPEG con otra extensión)
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0002.HEIC", 500, seed: 9);                  // ilegible para Windows
        var originalSha = Sandbox.Sha(Path.Combine(sb.Source, "DCIM", "100APPLE", "IMG_0001.HEIC"));

        var (session, device) = Make(new FlakyProvider(sb.Source));
        var opts = Options(sb);
        opts.AlsoJpg = true;
        try
        {
            var s = await session.RunAsync(device, opts, null, null, default);

            Assert.Equal(2, s.Copied);
            Assert.Equal(0, s.Failed);
            Assert.Equal(1, s.JpgCreated);
            Assert.Equal(1, s.JpgFailed);
            Assert.True(File.Exists(Path.Combine(opts.JpgRoot, "DCIM", "100APPLE", "IMG_0001.jpg")));
            Assert.Equal(originalSha, Sandbox.Sha(sb.DestFile("DCIM\\100APPLE\\IMG_0001.HEIC"))); // original intacto
            Assert.Contains("Copias JPG: 1 creadas, 1 no se pudieron crear", s.ReportText);
        }
        finally { try { Directory.Delete(opts.JpgRoot, true); } catch { } }
    }

    [Fact]
    public async Task Unreadable_heic_gives_a_notice_after_copying()
    {
        using var sb = new Sandbox();
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0002.HEIC", 500, seed: 9);
        var (session, device) = Make(new FlakyProvider(sb.Source));
        var s = await session.RunAsync(device, Options(sb), null, null, default);
        Assert.False(s.HeicReadable);
        Assert.Contains(s.Notices, n => n.Contains("Extensiones de imagen HEIF"));
        Assert.Equal(0, s.JpgCreated + s.JpgFailed); // la casilla JPG está desactivada por defecto
    }

    // ------------------------------------------------------------------ Ediciones IMG_E / .AAE

    [Fact]
    public void Edited_versions_are_paired_and_setting_aside_the_original_moves_without_deleting()
    {
        using var sb = new Sandbox();
        var root = sb.Dest;
        Jpeg(Path.Combine(root, "DCIM", "100APPLE", "IMG_0001.JPG"), seed: 1);
        File.Copy(Path.Combine(root, "DCIM", "100APPLE", "IMG_0001.JPG"), Path.Combine(root, "DCIM", "100APPLE", "IMG_E0001.JPG"));
        File.WriteAllText(Path.Combine(root, "DCIM", "100APPLE", "IMG_0001.AAE"), "<plist/>");
        Jpeg(Path.Combine(root, "DCIM", "100APPLE", "IMG_0002.JPG"), seed: 2);

        var pairs = EditedVersions.Find(MediaScanner.Scan(root));
        var p = Assert.Single(pairs);
        Assert.EndsWith("IMG_0001.JPG", p.Original.RelativePath);
        Assert.NotNull(p.Sidecar);

        // No son repetidas: una editada y su original no forman grupo.
        Assert.Empty(DuplicateService.Find(root, Album.Load(root).Items));

        var plan = EditedVersions.PlanSetAsideOriginals(root);
        Assert.Equal(2, plan.Items.Count); // original + .AAE; la simulación no toca nada
        Assert.True(File.Exists(Path.Combine(root, "DCIM", "100APPLE", "IMG_0001.JPG")));

        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Count(f => !f.Contains(".iphotos"));
        var result = new EditedVersions(new ActivityLog()).Apply(root, null, plan, null, default);
        Assert.Equal(2, result.Moved);
        Assert.True(File.Exists(Path.Combine(root, EditedVersions.Folder, "DCIM", "100APPLE", "IMG_0001.JPG")));
        Assert.True(File.Exists(Path.Combine(root, "DCIM", "100APPLE", "IMG_E0001.JPG")));
        Assert.Equal(before, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Count(f => !f.Contains(".iphotos"))); // nada borrado
        Assert.Empty(EditedVersions.Find(MediaScanner.Scan(root))); // la carpeta de apartadas no se vuelve a proponer
    }

    [Fact]
    public async Task Copy_report_counts_edited_versions()
    {
        using var sb = new Sandbox();
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0001.HEIC", 300, seed: 1);
        sb.AddPhoto("DCIM\\100APPLE\\IMG_E0001.HEIC", 310, seed: 2);
        var (session, device) = Make(new FlakyProvider(sb.Source));
        var s = await session.RunAsync(device, Options(sb), null, null, default);
        Assert.Equal(1, s.EditedVersions);
        Assert.Contains("No son repetidas", s.ReportText);
    }

    // ------------------------------------------------------------------ Vista grande

    [Fact]
    public void Preview_navigation_wraps_and_zoom_is_clamped()
    {
        var nav = new PreviewNavigator(3, 2);
        Assert.Equal("3 de 3", nav.Position);
        Assert.Equal(0, nav.Next());
        Assert.Equal(2, nav.Previous());
        Assert.Null(nav.Zoom);
        Assert.Equal(0.5 * PreviewNavigator.Step, nav.ZoomIn(0.5), 6); // parte de lo que se ve ajustado
        Assert.Equal(1, nav.ActualSize());
        for (var i = 0; i < 40; i++) nav.ZoomIn(1);
        Assert.Equal(PreviewNavigator.MaxZoom, nav.Zoom);
        for (var i = 0; i < 80; i++) nav.ZoomOut(1);
        Assert.Equal(PreviewNavigator.MinZoom, nav.Zoom);
        nav.Next();
        Assert.Null(nav.Zoom); // cada foto nueva empieza ajustada
        Assert.Equal(0, new PreviewNavigator(5, -3).Index);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewNavigator(0, 0));
    }
}
