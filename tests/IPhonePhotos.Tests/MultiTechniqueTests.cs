using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Transfer;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>
/// Varias técnicas para el mismo iPhone (se simulan con carpetas que comparten "número de serie"):
/// elección automática, cambio de técnica cuando una falla y uso de la copia de seguridad como último recurso.
/// </summary>
public class MultiTechniqueTests
{
    private static TransferOptions Options(Sandbox sb) => new()
    {
        DestinationRoot = sb.Dest,
        Retries = 1,
        RetryDelay = TimeSpan.FromMilliseconds(5),
        ReconnectTimeout = TimeSpan.FromSeconds(5),
        ReconnectPollInterval = TimeSpan.FromMilliseconds(10)
    };

    private static (TransferSession Session, BackendSelector Selector) Make(params FlakyProvider[] providers)
    {
        var log = new ActivityLog();
        var selector = new BackendSelector(providers, log);
        return (new TransferSession(selector, log), selector);
    }

    [Fact]
    public void Same_iphone_seen_by_several_techniques_appears_once()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(3);
        var wpd = new FlakyProvider(sb.Source, "Técnica A", "a", priority: 50);
        var afc = new FlakyProvider(sb.Source, "Técnica B", "b", priority: 60);
        var snap = new FlakyProvider(sb.Source, "Copia", "backup", priority: 0, isSnapshot: true);
        var (_, selector) = Make(wpd, afc, snap);

        var devices = selector.DiscoverAll();
        Assert.Single(devices);

        var techniques = selector.Techniques(devices[0]);
        Assert.Equal(new[] { "Técnica B", "Técnica A", "Copia" }, techniques.Select(t => t.Name)); // en vivo por prioridad; copia al final
    }

    [Fact]
    public async Task Technique_that_fails_to_open_is_skipped()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(5);
        var broken = new FlakyProvider(sb.Source, "Rota", "broken", priority: 90) { FailToOpen = true };
        var good = new FlakyProvider(sb.Source, "Buena", "good", priority: 50);
        var (session, selector) = Make(broken, good);

        var summary = await session.RunAsync(selector.DiscoverAll().Single(), Options(sb), null, null, default);

        Assert.Equal(5, summary.Copied);
        Assert.Equal(new[] { "Buena" }, summary.TechniquesUsed);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task Technique_that_shows_no_photos_is_skipped()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(5);
        var empty = new FlakyProvider(sb.Source, "Vacía", "empty", priority: 90) { ShowNothing = true };
        var good = new FlakyProvider(sb.Source, "Buena", "good", priority: 50);
        var (session, selector) = Make(empty, good);

        var summary = await session.RunAsync(selector.DiscoverAll().Single(), Options(sb), null, null, default);

        Assert.Equal(5, summary.Copied);
        Assert.Contains("Buena", summary.TechniquesUsed);
    }

    [Fact]
    public async Task Files_that_one_technique_cannot_read_are_retried_with_the_next_technique()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(6);
        var bad = "DCIM\\100APPLE\\IMG_0002.HEIC";
        var first = new FlakyProvider(sb.Source, "Primera", "first", priority: 90)
        {
            ConfigureConnection = (n, b) => { b.AlwaysFail.Add(bad); return b; }
        };
        var second = new FlakyProvider(sb.Source, "Segunda", "second", priority: 50);
        var (session, selector) = Make(first, second);

        var summary = await session.RunAsync(selector.DiscoverAll().Single(), Options(sb), null, null, default);

        Assert.Equal(0, summary.Failed);
        Assert.Equal(6, summary.Copied);
        Assert.Equal(new[] { "Primera", "Segunda" }, summary.TechniquesUsed);
        Assert.True(File.Exists(sb.DestFile(bad)));
        Assert.Equal(Sandbox.Sha(Path.Combine(sb.Source, bad)), Sandbox.Sha(sb.DestFile(bad)));
        // Y solo hubo una copia de cada foto: nada duplicado.
        Assert.Equal(6, sb.DestFiles().Count());
    }

    [Fact]
    public async Task Snapshot_is_never_used_when_a_live_technique_works()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(4);
        var live = new FlakyProvider(sb.Source, "En vivo", "live", priority: 50);
        var snap = new FlakyProvider(sb.Source, "Copia", "backup", priority: 0, isSnapshot: true);
        var (session, selector) = Make(live, snap);

        var summary = await session.RunAsync(selector.DiscoverAll().Single(), Options(sb), null, null, default);

        Assert.Equal(4, summary.Copied);
        Assert.Equal(0, snap.Opens);
        Assert.Null(summary.SnapshotWarning);
    }

    [Fact]
    public async Task Snapshot_is_used_as_last_resort_with_a_warning()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(4);
        var live = new FlakyProvider(sb.Source, "En vivo", "live", priority: 50) { FailToOpen = true };
        var snap = new FlakyProvider(sb.Source, "Copia", "backup", priority: 0, isSnapshot: true);
        var (session, selector) = Make(live, snap);

        var summary = await session.RunAsync(selector.DiscoverAll().Single(), Options(sb), null, null, default);

        Assert.Equal(4, summary.Copied);
        Assert.Equal(1, snap.Opens);
        Assert.NotNull(summary.SnapshotWarning);
    }

    [Fact]
    public async Task Snapshot_is_skipped_when_the_user_disallows_it()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(3);
        var live = new FlakyProvider(sb.Source, "En vivo", "live", priority: 50) { FailToOpen = true };
        var snap = new FlakyProvider(sb.Source, "Copia", "backup", priority: 0, isSnapshot: true);
        var (session, selector) = Make(live, snap);
        var opts = Options(sb);
        opts.AllowSnapshotFallback = false;

        await Assert.ThrowsAsync<DeviceLostException>(() => session.RunAsync(selector.DiscoverAll().Single(), opts, null, null, default));
        Assert.Equal(0, snap.Opens);
        Assert.Empty(sb.DestFiles());
    }

    [Fact]
    public async Task Only_technique_option_restricts_the_choice()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(3);
        var a = new FlakyProvider(sb.Source, "A", "a", priority: 90);
        var b = new FlakyProvider(sb.Source, "B", "b", priority: 50);
        var (session, selector) = Make(a, b);
        var opts = Options(sb);
        opts.OnlyTechnique = "b";

        var summary = await session.RunAsync(selector.DiscoverAll().Single(), opts, null, null, default);

        Assert.Equal(new[] { "B" }, summary.TechniquesUsed);
        Assert.Equal(0, a.Opens);
    }

    [Fact]
    public async Task Resume_continues_across_a_technique_change()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(8);
        // La primera técnica se corta tras 3 archivos y no vuelve; la segunda sigue disponible.
        var first = new FlakyProvider(sb.Source, "Primera", "first", priority: 90)
        {
            ConfigureConnection = (n, b) => { b.UnplugAfterFiles = 3; return b; },
            ReappearAfterPolls = int.MaxValue
        };
        var second = new FlakyProvider(sb.Source, "Segunda", "second", priority: 50);
        var (session, selector) = Make(first, second);

        var summary = await session.RunAsync(selector.DiscoverAll().Single(), Options(sb), null, null, default);

        // Copied cuenta copias reales: si la segunda técnica hubiera repetido lo ya copiado por la primera, sería > 8.
        Assert.Equal(8, summary.Copied);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(8, sb.DestFiles().Count());
        Assert.Equal(1, summary.Reconnects);
        Assert.Equal(new[] { "Primera", "Segunda" }, summary.TechniquesUsed);
        foreach (var src in Directory.EnumerateFiles(sb.Source, "*", SearchOption.AllDirectories))
            Assert.Equal(Sandbox.Sha(src), Sandbox.Sha(sb.DestFile(Path.GetRelativePath(sb.Source, src))));
    }

    [Fact]
    public void Dates_from_different_techniques_still_match_across_time_zones()
    {
        var local = new DateTime(2024, 5, 1, 12, 30, 15);
        Assert.True(FileIdentity.DatesMatch(local, local));
        Assert.True(FileIdentity.DatesMatch(local, local.AddSeconds(1)));
        Assert.True(FileIdentity.DatesMatch(local, local.AddHours(2)));
        Assert.True(FileIdentity.DatesMatch(local, local.AddHours(-5).AddMinutes(-30)));
        Assert.False(FileIdentity.DatesMatch(local, null)); // v1.5: una fecha que falta ya no vale como coincidencia
        Assert.False(FileIdentity.DatesMatch(local, local.AddMinutes(7)));
        Assert.False(FileIdentity.DatesMatch(local, local.AddSeconds(40)));
    }
}
