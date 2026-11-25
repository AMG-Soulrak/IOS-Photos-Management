using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Transfer;
using Xunit;

namespace IPhonePhotos.Tests;

public class TransferTests
{
    private static (TransferSession Session, BackendSelector Selector, ActivityLog Log) Make(FlakyProvider provider)
    {
        var log = new ActivityLog();
        var selector = new BackendSelector(new[] { provider }, log);
        return (new TransferSession(selector, log), selector, log);
    }

    private static TransferOptions Options(Sandbox sb, AdoptMode adopt = AdoptMode.Fast) => new()
    {
        DestinationRoot = sb.Dest,
        Adopt = adopt,
        Retries = 2,
        RetryDelay = TimeSpan.FromMilliseconds(5),
        ReconnectTimeout = TimeSpan.FromSeconds(10),
        ReconnectPollInterval = TimeSpan.FromMilliseconds(10)
    };

    private static DeviceDescriptor Device(FlakyProvider p) => p.Discover().Single();

    [Fact]
    public async Task Copies_everything_keeping_structure_and_content()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(10);
        var provider = new FlakyProvider(sb.Source);
        var (session, _, _) = Make(provider);

        var summary = await session.RunAsync(Device(provider), Options(sb), null, null, default);

        Assert.Equal(10, summary.Copied);
        Assert.Equal(0, summary.Failed);
        foreach (var src in Directory.EnumerateFiles(sb.Source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sb.Source, src);
            Assert.True(File.Exists(sb.DestFile(rel)), $"falta {rel}");
            Assert.Equal(Sandbox.Sha(src), Sandbox.Sha(sb.DestFile(rel)));
        }
        Assert.Empty(sb.DestFiles().Where(f => f.EndsWith(".part")));
    }

    [Fact]
    public async Task Second_run_copies_nothing_again()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(8);
        var provider = new FlakyProvider(sb.Source);
        var (session, _, _) = Make(provider);

        await session.RunAsync(Device(provider), Options(sb), null, null, default);
        var second = await session.RunAsync(Device(provider), Options(sb), null, null, default);

        Assert.Equal(0, second.Copied);
        Assert.Equal(8, second.AlreadyDone);
        Assert.All(provider.CompletedCopies.Values, v => Assert.Equal(1, v));
    }

    [Fact]
    public async Task Cable_unplugged_mid_copy_waits_reconnects_and_resumes_without_recopying()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(10);
        var provider = new FlakyProvider(sb.Source)
        {
            // La primera conexión se corta al llegar al archivo nº 4; la segunda es normal.
            ConfigureConnection = (n, b) => { if (n == 1) b.UnplugAfterFiles = 3; return b; }
        };
        var (session, _, _) = Make(provider);

        var summary = await session.RunAsync(Device(provider), Options(sb), null, null, default);

        Assert.Equal(1, summary.Reconnects);
        Assert.Equal(10, summary.Copied);
        Assert.Equal(0, summary.AlreadyDone); // nada estaba copiado antes de empezar
        Assert.Equal(0, summary.Failed);
        Assert.Equal(2, provider.Opens);
        // Cada archivo se completó exactamente una vez: lo ya descargado no se repitió.
        Assert.Equal(10, provider.CompletedCopies.Count);
        Assert.All(provider.CompletedCopies.Values, v => Assert.Equal(1, v));
        foreach (var src in Directory.EnumerateFiles(sb.Source, "*", SearchOption.AllDirectories))
            Assert.Equal(Sandbox.Sha(src), Sandbox.Sha(sb.DestFile(Path.GetRelativePath(sb.Source, src))));
        Assert.Empty(sb.DestFiles().Where(f => f.EndsWith(".part")));
    }

    [Fact]
    public async Task Cable_unplugged_during_scan_also_waits_and_resumes()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(6);
        var provider = new FlakyProvider(sb.Source)
        {
            ConfigureConnection = (n, b) => { if (n == 1) b.DieDuringEnumeration = true; return b; }
        };
        var (session, _, _) = Make(provider);

        var summary = await session.RunAsync(Device(provider), Options(sb), null, null, default);

        Assert.Equal(1, summary.Reconnects);
        Assert.Equal(6, summary.Copied);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task Cancel_in_the_middle_leaves_no_partial_file_and_next_run_finishes()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(6);
        var hang = "DCIM\\100APPLE\\IMG_0003.HEIC";
        var provider = new FlakyProvider(sb.Source)
        {
            ConfigureConnection = (n, b) => { if (n == 1) b.HangOn = hang; return b; }
        };
        var (session, _, _) = Make(provider);

        using var cts = new CancellationTokenSource();
        var run = session.RunAsync(Device(provider), Options(sb), null, null, cts.Token);
        await Task.Delay(400);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Empty(sb.DestFiles().Where(f => f.EndsWith(".part")));
        Assert.False(File.Exists(sb.DestFile(hang)));
        Assert.Equal(2, sb.DestFiles().Count());

        // Segunda ejecución sin fallos: termina lo que faltaba sin repetir lo hecho.
        var second = await session.RunAsync(Device(provider), Options(sb), null, null, default);
        Assert.Equal(4, second.Copied);
        Assert.Equal(2, second.AlreadyDone);
        Assert.Equal(6, sb.DestFiles().Count());
    }

    [Fact]
    public async Task Transient_errors_are_retried()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(4);
        var provider = new FlakyProvider(sb.Source)
        {
            ConfigureConnection = (n, b) => { b.TransientFailuresPerFile = 2; return b; }
        };
        var (session, _, _) = Make(provider);

        var summary = await session.RunAsync(Device(provider), Options(sb), null, null, default);

        Assert.Equal(4, summary.Copied);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Reconnects);
    }

    [Fact]
    public async Task Unreadable_file_is_reported_and_does_not_stop_the_rest()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(5);
        var bad = "DCIM\\100APPLE\\IMG_0002.HEIC";
        var provider = new FlakyProvider(sb.Source)
        {
            ConfigureConnection = (n, b) => { b.AlwaysFail.Add(bad); return b; }
        };
        var (session, _, _) = Make(provider);

        var summary = await session.RunAsync(Device(provider), Options(sb), null, null, default);

        Assert.Equal(4, summary.Copied);
        Assert.Equal(1, summary.Failed);
        Assert.Contains(bad, summary.FailedFiles);
        Assert.False(File.Exists(sb.DestFile(bad)));
    }

    [Fact]
    public async Task Deleted_local_file_is_downloaded_again()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(4);
        var provider = new FlakyProvider(sb.Source);
        var (session, _, _) = Make(provider);
        await session.RunAsync(Device(provider), Options(sb), null, null, default);

        var gone = "DCIM\\100APPLE\\IMG_0001.HEIC";
        File.Delete(sb.DestFile(gone));

        var second = await session.RunAsync(Device(provider), Options(sb), null, null, default);
        Assert.Equal(1, second.Copied);
        Assert.True(File.Exists(sb.DestFile(gone)));
    }

    [Fact]
    public async Task Photo_moved_to_another_folder_outside_the_app_is_not_downloaded_again()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(4);
        var provider = new FlakyProvider(sb.Source);
        var (session, _, _) = Make(provider);
        await session.RunAsync(Device(provider), Options(sb), null, null, default);

        // El usuario reparte la foto en un álbum con el Explorador: la app no se entera.
        var original = "DCIM\\100APPLE\\IMG_0001.HEIC";
        var album = "Álbumes\\Viaje\\IMG_0001.HEIC";
        Directory.CreateDirectory(Path.GetDirectoryName(sb.DestFile(album))!);
        File.Move(sb.DestFile(original), sb.DestFile(album));

        var second = await session.RunAsync(Device(provider), Options(sb), null, null, default);

        Assert.Equal(0, second.Copied);
        Assert.Equal(4, second.AlreadyDone);
        Assert.False(File.Exists(sb.DestFile(original)), "no debe volver a bajarse a su sitio original");
        Assert.All(provider.CompletedCopies.Values, v => Assert.Equal(1, v));
    }

    [Fact]
    public async Task Photo_already_in_the_library_without_a_manifest_record_is_not_downloaded()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(3);
        // La biblioteca ya tiene esa foto (copiada de otra forma, p. ej. con otra técnica), en un álbum y sin constar en el manifiesto.
        var rel = "DCIM\\100APPLE\\IMG_0001.HEIC";
        var inAlbum = "Álbumes\\Familia\\IMG_0001.HEIC";
        Directory.CreateDirectory(Path.GetDirectoryName(sb.DestFile(inAlbum))!);
        File.Copy(Path.Combine(sb.Source, rel), sb.DestFile(inAlbum));
        var provider = new FlakyProvider(sb.Source);
        var (session, _, _) = Make(provider);

        var summary = await session.RunAsync(Device(provider), Options(sb), null, null, default);

        Assert.Equal(2, summary.Copied);
        Assert.False(File.Exists(sb.DestFile(rel)));
        Assert.True(File.Exists(sb.DestFile(inAlbum)));
    }

    [Fact]
    public async Task New_photos_go_to_the_new_folder_and_ones_already_distributed_are_left_alone()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(3);
        var provider = new FlakyProvider(sb.Source);
        var (session, _, _) = Make(provider);
        var options = Options(sb);
        options.NewFolder = "Nuevos";
        await session.RunAsync(Device(provider), options, null, null, default);
        Assert.True(File.Exists(sb.DestFile("Nuevos\\IMG_0001.HEIC")));

        // Reparte una en un álbum; en el iPhone aparece una foto nueva.
        var album = "Álbumes\\Viaje\\IMG_0001.HEIC";
        Directory.CreateDirectory(Path.GetDirectoryName(sb.DestFile(album))!);
        File.Move(sb.DestFile("Nuevos\\IMG_0001.HEIC"), sb.DestFile(album));
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0099.HEIC", 4321, seed: 7);

        var second = await session.RunAsync(Device(provider), options, null, null, default);

        Assert.Equal(1, second.Copied);
        Assert.True(File.Exists(sb.DestFile("Nuevos\\IMG_0099.HEIC")), "la nueva debe ir a «Nuevos»");
        Assert.False(File.Exists(sb.DestFile("Nuevos\\IMG_0001.HEIC")), "la ya repartida no debe volver a «Nuevos»");
    }

    [Fact]
    public async Task Same_name_but_different_content_is_kept_separately()
    {
        using var sb = new Sandbox();
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0001.HEIC", 5000, seed: 1);
        var provider = new FlakyProvider(sb.Source);
        var (session, _, _) = Make(provider);
        await session.RunAsync(Device(provider), Options(sb), null, null, default);

        // El iPhone se restaura y vuelve a numerar: otra foto con el mismo nombre.
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0001.HEIC", 7777, seed: 99);
        var second = await session.RunAsync(Device(provider), Options(sb), null, null, default);

        Assert.Equal(1, second.Copied);
        Assert.True(File.Exists(sb.DestFile("DCIM\\100APPLE\\IMG_0001.HEIC")));
        Assert.True(File.Exists(sb.DestFile("DCIM\\100APPLE\\IMG_0001 (2).HEIC")));
        Assert.Equal(5000, new FileInfo(sb.DestFile("DCIM\\100APPLE\\IMG_0001.HEIC")).Length);
        Assert.Equal(7777, new FileInfo(sb.DestFile("DCIM\\100APPLE\\IMG_0001 (2).HEIC")).Length);
    }

    [Fact]
    public async Task Adopt_fast_reuses_files_already_in_destination_by_name_and_size()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(6);
        // El usuario ya había copiado 3 fotos a mano.
        foreach (var rel in new[] { "DCIM\\100APPLE\\IMG_0001.HEIC", "DCIM\\100APPLE\\IMG_0002.HEIC", "DCIM\\100APPLE\\IMG_0003.HEIC" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sb.DestFile(rel))!);
            File.Copy(Path.Combine(sb.Source, rel), sb.DestFile(rel));
        }
        var provider = new FlakyProvider(sb.Source);
        var (session, _, _) = Make(provider);

        var summary = await session.RunAsync(Device(provider), Options(sb, AdoptMode.Fast), null, null, default);

        Assert.Equal(3, summary.Copied);
        Assert.Equal(3, summary.Adopted);
        Assert.Equal(3, provider.CompletedCopies.Count);
    }

    [Fact]
    public async Task Adopt_deep_compares_content_and_rejects_a_look_alike()
    {
        using var sb = new Sandbox();
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0001.HEIC", 3000, seed: 1);
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0002.HEIC", 3000, seed: 2);

        // IMG_0001: copia idéntica. IMG_0002: mismo nombre y tamaño pero contenido distinto.
        Directory.CreateDirectory(Path.Combine(sb.Dest, "DCIM", "100APPLE"));
        File.Copy(Path.Combine(sb.Source, "DCIM\\100APPLE\\IMG_0001.HEIC"), sb.DestFile("DCIM\\100APPLE\\IMG_0001.HEIC"));
        File.WriteAllBytes(sb.DestFile("DCIM\\100APPLE\\IMG_0002.HEIC"), Sandbox.Bytes(3000, 555));
        File.SetLastWriteTime(sb.DestFile("DCIM\\100APPLE\\IMG_0002.HEIC"), File.GetLastWriteTime(Path.Combine(sb.Source, "DCIM\\100APPLE\\IMG_0002.HEIC")));

        var provider = new FlakyProvider(sb.Source);
        var (session, _, _) = Make(provider);
        var summary = await session.RunAsync(Device(provider), Options(sb, AdoptMode.Deep), null, null, default);

        Assert.Equal(1, summary.Adopted);
        Assert.Equal(1, summary.Copied);
        Assert.True(File.Exists(sb.DestFile("DCIM\\100APPLE\\IMG_0002 (2).HEIC")));
        // El archivo previo distinto no se toca.
        Assert.Equal(Sandbox.Bytes(3000, 555), File.ReadAllBytes(sb.DestFile("DCIM\\100APPLE\\IMG_0002.HEIC")));
    }

    [Fact]
    public async Task Wait_gives_up_after_timeout_and_progress_is_kept()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(6);
        var provider = new FlakyProvider(sb.Source)
        {
            ReappearAfterPolls = int.MaxValue, // nunca vuelve
            ConfigureConnection = (n, b) => { b.UnplugAfterFiles = 2; return b; }
        };
        var (session, _, _) = Make(provider);
        var opts = Options(sb);
        opts.ReconnectTimeout = TimeSpan.FromMilliseconds(200);

        await Assert.ThrowsAsync<DeviceLostException>(() => session.RunAsync(Device(provider), opts, null, null, default));
        Assert.Equal(2, sb.DestFiles().Count());

        // El usuario lo vuelve a enchufar y relanza: continúa por lo que faltaba.
        provider.Present = true;
        provider.ConfigureConnection = null;
        var second = await session.RunAsync(Device(provider), Options(sb), null, null, default);
        Assert.Equal(4, second.Copied);
        Assert.Equal(2, second.AlreadyDone);
    }

    [Fact]
    public void Hashing_stream_matches_sha256()
    {
        var data = Sandbox.Bytes(100_000, 3);
        using var hs = new HashingWriteStream(null);
        hs.Write(data, 0, 40_000);
        hs.Write(data.AsSpan(40_000));
        Assert.Equal(100_000, hs.BytesWritten);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(), hs.GetHashHex());
    }
}
