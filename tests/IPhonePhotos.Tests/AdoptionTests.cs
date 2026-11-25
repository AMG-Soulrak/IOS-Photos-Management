using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Transfer;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>
/// Adopción rápida (v1.5): lo que ya estaba en la carpeta con el mismo nombre, tamaño y fecha no se descarga otra vez,
/// pero queda «adoptado, sin verificar» y nunca cuenta como verificado hasta contrastarlo con el iPhone.
/// Se comprueba el ESTADO de cada registro, no solo los contadores.
/// </summary>
public class AdoptionTests
{
    private static TransferOptions Options(Sandbox sb) => new()
    {
        DestinationRoot = sb.Dest,
        Adopt = AdoptMode.Fast,
        Retries = 1,
        RetryDelay = TimeSpan.FromMilliseconds(5),
        ReconnectPollInterval = TimeSpan.FromMilliseconds(10)
    };

    /// <summary>Copia a mano al destino conservando la fecha (como el Explorador de Windows).</summary>
    private static void PreCopy(Sandbox sb, string rel, byte[]? otherContent = null)
    {
        var src = Path.Combine(sb.Source, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(sb.DestFile(rel))!);
        if (otherContent == null) File.Copy(src, sb.DestFile(rel));
        else File.WriteAllBytes(sb.DestFile(rel), otherContent);
        File.SetLastWriteTime(sb.DestFile(rel), File.GetLastWriteTime(src));
    }

    [Fact]
    public void A_missing_date_is_not_a_match()
    {
        var d = new DateTime(2024, 1, 1, 12, 0, 0);
        Assert.False(FileIdentity.DatesMatch(null, d));
        Assert.False(FileIdentity.DatesMatch(d, null));
        Assert.True(FileIdentity.DatesMatch(null, null)); // el mismo registro visto otra vez sin fecha
        Assert.True(FileIdentity.DatesMatch(d, d.AddHours(1).AddSeconds(1))); // desfase de zona horaria
        Assert.False(FileIdentity.DatesMatch(d, d.AddSeconds(30)));
    }

    [Fact]
    public async Task Fast_adoption_is_never_verified_until_the_deep_check()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(4);
        PreCopy(sb, "DCIM\\100APPLE\\IMG_0001.HEIC");
        PreCopy(sb, "DCIM\\100APPLE\\IMG_0002.HEIC");
        var provider = new FlakyProvider(sb.Source);
        var log = new ActivityLog();
        var selector = new BackendSelector(new[] { provider }, log);

        var summary = await new TransferSession(selector, log).RunAsync(provider.Discover().Single(), Options(sb), null, null, default);
        Assert.Equal(2, summary.Adopted);
        Assert.Equal(2, summary.Copied);

        using var store = ManifestStore.Open(sb.Dest);
        Assert.Equal(2, store.InState(FileState.AdoptedUnverified).Count);

        var svc = new VerificationService(log);
        var local = await svc.VerifyLocalAsync(store, sb.Dest, true, null, default);
        Assert.Equal(2, local.Verified);             // solo los descargados por la app (tienen hash de la copia)
        Assert.Equal(2, local.AdoptedUnverified);
        Assert.Equal(2, store.InState(FileState.AdoptedUnverified).Count);

        using var backend = provider.Open(provider.Discover().Single());
        var deep = await svc.VerifyDeepAsync(backend, store, sb.Dest, null, default);
        Assert.Equal(4, deep.Verified);
        Assert.Equal(4, store.InState(FileState.Verified).Count);
        Assert.Empty(store.InState(FileState.AdoptedUnverified));
    }

    [Fact]
    public async Task Look_alike_adopted_file_is_caught_by_the_deep_check_and_downloaded_aside()
    {
        using var sb = new Sandbox();
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0001.HEIC", 3000, seed: 1);
        PreCopy(sb, "DCIM\\100APPLE\\IMG_0001.HEIC", Sandbox.Bytes(3000, 999)); // mismo nombre, tamaño y fecha; otro contenido
        var provider = new FlakyProvider(sb.Source);
        var device = provider.Discover().Single();
        var log = new ActivityLog();
        var session = new TransferSession(new BackendSelector(new[] { provider }, log), log);

        var first = await session.RunAsync(device, Options(sb), null, null, default);
        Assert.Equal(1, first.Adopted);

        using (var store = ManifestStore.Open(sb.Dest))
        using (var backend = provider.Open(device))
        {
            var deep = await new VerificationService(log).VerifyDeepAsync(backend, store, sb.Dest, null, default);
            Assert.Equal(1, deep.Corrupt);
            Assert.Equal(FileState.Discovered, store.All().Single().State); // ya no cuenta como copiado
        }

        var second = await session.RunAsync(device, Options(sb), null, null, default);
        Assert.Equal(1, second.Copied);
        Assert.Equal(Sandbox.Sha(Path.Combine(sb.Source, "DCIM\\100APPLE\\IMG_0001.HEIC")), Sandbox.Sha(sb.DestFile("DCIM\\100APPLE\\IMG_0001 (2).HEIC")));
        Assert.Equal(Sandbox.Bytes(3000, 999), File.ReadAllBytes(sb.DestFile("DCIM\\100APPLE\\IMG_0001.HEIC"))); // el dudoso no se toca
    }

    [Fact]
    public void A_device_without_dates_never_adopts_by_name_and_size()
    {
        using var sb = new Sandbox();
        sb.AddPhoto("DCIM\\100APPLE\\IMG_0001.HEIC", 3000, seed: 1);
        PreCopy(sb, "DCIM\\100APPLE\\IMG_0001.HEIC");
        var provider = new FlakyProvider(sb.Source);
        using var backend = new NoDates(provider.Open(provider.Discover().Single()));
        using var store = ManifestStore.Open(sb.Dest);

        var scan = new TransferEngine(new ActivityLog()).Scan(backend, store, Options(sb), null, default);

        Assert.Equal(0, scan.Adopted);
        Assert.Single(scan.Pending);
    }

    [Fact]
    public async Task Old_manifest_fast_adoptions_marked_verified_are_downgraded_on_open()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(2);
        var provider = new FlakyProvider(sb.Source);
        var log = new ActivityLog();
        await new TransferSession(new BackendSelector(new[] { provider }, log), log)
            .RunAsync(provider.Discover().Single(), Options(sb), null, null, default);
        using (var store = ManifestStore.Open(sb.Dest))
        {
            // Así quedaba en v1.4 un archivo adoptado rápido tras «Verificar copias».
            var r = store.All().First();
            r.State = FileState.Verified;
            r.Note = "adoptado (nombre y tamaño)";
            store.Upsert(r);
        }

        using var reopened = ManifestStore.Open(sb.Dest);
        Assert.Single(reopened.InState(FileState.AdoptedUnverified));
        Assert.Single(Directory.GetFiles(ManifestStore.MetaFolder(sb.Dest), "manifest.db.bak-*"));
    }

    /// <summary>Una técnica que no informa fechas (algunas copias de seguridad con metadatos ilegibles).</summary>
    private sealed class NoDates : IDeviceBackend
    {
        private readonly IDeviceBackend _inner;
        public NoDates(IDeviceBackend inner) => _inner = inner;
        public DeviceDescriptor Device => _inner.Device;
        public BackendCapabilities Capabilities => _inner.Capabilities;
        public IEnumerable<RemoteFile> EnumerateMedia(CancellationToken ct) => _inner.EnumerateMedia(ct).Select(f => f with { Modified = null });
        public Task CopyToAsync(RemoteFile file, Stream destination, CancellationToken ct) => _inner.CopyToAsync(file, destination, ct);
        public bool IsAlive() => _inner.IsAlive();
        public void Dispose() => _inner.Dispose();
    }
}
