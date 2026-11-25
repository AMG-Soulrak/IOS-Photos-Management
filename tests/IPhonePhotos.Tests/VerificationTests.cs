using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Transfer;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>
/// Verificación de lo copiado. Las 10 pruebas de borrado del iPhone se eliminaron en v1.5 junto con la función
/// (ADR-0001: la app solo lee del dispositivo); su sustituto es la prueba de migración de manifiestos antiguos.
/// </summary>
public class VerificationTests
{
    private static TransferOptions Options(Sandbox sb) => new()
    {
        DestinationRoot = sb.Dest,
        Retries = 1,
        RetryDelay = TimeSpan.FromMilliseconds(5),
        ReconnectPollInterval = TimeSpan.FromMilliseconds(10)
    };

    private sealed class Ctx : IDisposable
    {
        public Sandbox Sb = new();
        public FlakyProvider Provider;
        public ActivityLog Log = new();
        public BackendSelector Selector;
        public DeviceDescriptor Device;

        public Ctx(int photos) : this(sb => sb.AddLibrary(photos)) { }

        public Ctx(Action<Sandbox> fill)
        {
            fill(Sb);
            Provider = new FlakyProvider(Sb.Source);
            Selector = new BackendSelector(new[] { Provider }, Log);
            Device = Provider.Discover().Single();
        }

        public Task<TransferSummary> Download() =>
            new TransferSession(Selector, Log).RunAsync(Device, Options(Sb), null, null, default);

        public ManifestStore Store() => ManifestStore.Open(Sb.Dest);

        public IDeviceBackend Open() => Provider.Open(Device);

        public void Dispose() => Sb.Dispose();
    }

    [Fact]
    public async Task Local_verification_marks_files_verified()
    {
        using var c = new Ctx(5);
        await c.Download();
        using var store = c.Store();

        var r = await new VerificationService(c.Log).VerifyLocalAsync(store, c.Sb.Dest, false, null, default);

        Assert.Equal(5, r.Verified);
        Assert.Equal(5, store.InState(FileState.Verified).Count);
    }

    [Fact]
    public async Task Local_verification_detects_a_modified_file_and_redownloads_it_aside()
    {
        using var c = new Ctx(3);
        await c.Download();
        var rel = "DCIM\\100APPLE\\IMG_0001.HEIC";
        var path = c.Sb.DestFile(rel);
        var bytes = File.ReadAllBytes(path);
        bytes[10] ^= 0xFF; // mismo tamaño, un byte distinto
        File.WriteAllBytes(path, bytes);

        using (var store = c.Store())
        {
            var r = await new VerificationService(c.Log).VerifyLocalAsync(store, c.Sb.Dest, false, null, default);
            Assert.Equal(1, r.Corrupt);
            Assert.Equal(2, r.Verified);
        }

        var again = await c.Download();
        Assert.Equal(1, again.Copied);
        // El archivo dudoso no se borra ni se sobrescribe: la copia buena va a otro nombre.
        Assert.True(File.Exists(c.Sb.DestFile("DCIM\\100APPLE\\IMG_0001 (2).HEIC")));
        Assert.Equal(Sandbox.Sha(Path.Combine(c.Sb.Source, rel)), Sandbox.Sha(c.Sb.DestFile("DCIM\\100APPLE\\IMG_0001 (2).HEIC")));
    }

    [Fact]
    public async Task Deep_verification_catches_a_mismatch_with_the_device()
    {
        using var c = new Ctx(3);
        await c.Download();
        // El archivo del iPhone cambia después de la copia (mismo tamaño).
        var src = Directory.GetFiles(c.Sb.Source, "IMG_0002.HEIC", SearchOption.AllDirectories).Single();
        var info = new FileInfo(src);
        var stamp = info.LastWriteTime;
        File.WriteAllBytes(src, Sandbox.Bytes((int)info.Length, 4242));
        File.SetLastWriteTime(src, stamp);

        using var backend = c.Open();
        using var store = c.Store();
        var svc = new VerificationService(c.Log);
        await svc.VerifyLocalAsync(store, c.Sb.Dest, false, null, default);
        var r = await svc.VerifyDeepAsync(backend, store, c.Sb.Dest, null, default);

        Assert.Equal(2, r.Verified);
        Assert.Equal(1, r.Corrupt);
    }

    [Fact]
    public async Task Old_manifest_with_deleted_records_is_backed_up_and_migrated_to_verified()
    {
        using var c = new Ctx(3);
        await c.Download();
        using (var store = c.Store())
        {
            var rec = store.All().First();
#pragma warning disable CS0618 // se simula un manifiesto de v1.4
            rec.State = FileState.Deleted;
#pragma warning restore CS0618
            rec.DeletedAtUtc = DateTime.UtcNow;
            store.Upsert(rec);
        }

        using (var store = c.Store())
        {
            Assert.Single(store.InState(FileState.Verified)); // el que constaba borrado; los otros siguen descargados
            Assert.All(store.All(), r => Assert.NotEqual("Deleted", r.State.ToString()));
            Assert.Single(store.All(), r => r.DeletedAtUtc != null); // el dato histórico se conserva

            // Sigue contando como ya copiado: no se vuelve a descargar ni a reintentar.
            using var backend = c.Open();
            var scan = new TransferEngine(c.Log).Scan(backend, store, Options(c.Sb), null, default);
            Assert.Equal(3, scan.AlreadyDone);
            Assert.Empty(scan.Pending);
        }

        var meta = ManifestStore.MetaFolder(c.Sb.Dest);
        Assert.Single(Directory.GetFiles(meta, $"manifest.db.bak-{DateTime.Now:yyyyMMdd}*"));
    }
}
