using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Organize;
using IPhonePhotos.Core.Transfer;
using Xunit;

namespace IPhonePhotos.Tests;

public class OrganizeTests
{
    private sealed class Ctx : IDisposable
    {
        public Sandbox Sb = new();
        public ActivityLog Log = new();
        public FlakyProvider Provider;
        public BackendSelector Selector;
        public DeviceDescriptor Device;

        public Ctx(Action<Sandbox> fill)
        {
            fill(Sb);
            Provider = new FlakyProvider(Sb.Source);
            Selector = new BackendSelector(new[] { Provider }, Log);
            Device = Provider.Discover().Single();
        }

        public TransferOptions Options(bool organize = false) => new()
        {
            DestinationRoot = Sb.Dest,
            OrganizeByDate = organize,
            Retries = 1,
            RetryDelay = TimeSpan.FromMilliseconds(5),
            ReconnectPollInterval = TimeSpan.FromMilliseconds(10)
        };

        public Task<TransferSummary> Download(bool organize = false) =>
            new TransferSession(Selector, Log).RunAsync(Device, Options(organize), null, null, default);

        public ManifestStore Store() => ManifestStore.Open(Sb.Dest);

        public OrganizeService Org => new(Log);
        public LivePhotoService Live => new(Log);

        public void Dispose() => Sb.Dispose();
    }

    private static void Photo(Sandbox sb, string rel, int seed, DateTime date)
    {
        var path = sb.AddPhoto(rel, 3000 + seed * 11, seed);
        File.SetLastWriteTime(path, date);
    }

    private static void Library(Sandbox sb)
    {
        Photo(sb, "DCIM\\100APPLE\\IMG_0001.HEIC", 1, new DateTime(2024, 1, 15, 10, 0, 0));
        Photo(sb, "DCIM\\100APPLE\\IMG_0002.HEIC", 2, new DateTime(2024, 3, 2, 9, 30, 0));
        Photo(sb, "DCIM\\101APPLE\\IMG_0003.HEIC", 3, new DateTime(2025, 12, 31, 20, 0, 0));
        Photo(sb, "DCIM\\101APPLE\\IMG_0004.HEIC", 4, new DateTime(2025, 12, 1, 8, 0, 0));
    }

    private static void LiveLibrary(Sandbox sb)
    {
        var t = new DateTime(2024, 5, 10, 12, 0, 0);
        Photo(sb, "DCIM\\100APPLE\\IMG_0001.HEIC", 1, t);
        Photo(sb, "DCIM\\100APPLE\\IMG_0001.MOV", 2, t.AddSeconds(1));      // clip del Live Photo
        Photo(sb, "DCIM\\100APPLE\\IMG_0002.MOV", 3, t.AddMinutes(5));      // vídeo normal (sin foto hermana)
        Photo(sb, "DCIM\\100APPLE\\IMG_0003.HEIC", 4, t.AddMinutes(9));     // foto normal
        Photo(sb, "DCIM\\100APPLE\\IMG_0004.JPG", 5, t.AddMinutes(20));
        Photo(sb, "DCIM\\100APPLE\\IMG_0004.MOV", 6, t.AddMinutes(25));     // mismo nombre pero fechas muy distintas: no es pareja
    }

    private static string Clip(params string[] parts) => Path.Combine(parts);

    // ------------------------------------------------------------------ Organizar por fecha (carpeta cualquiera)

    [Fact]
    public void DateLayout_maps_year_and_month_and_handles_missing_dates()
    {
        Assert.Equal(Path.Combine("2024", "03", "IMG_1.HEIC"), DateLayout.Map("DCIM\\100APPLE\\IMG_1.HEIC", new DateTime(2024, 3, 9)));
        Assert.Equal(Path.Combine(DateLayout.NoDateFolder, "IMG_1.HEIC"), DateLayout.Map("DCIM\\100APPLE\\IMG_1.HEIC", null));
        Assert.Equal(Path.Combine(DateLayout.NoDateFolder, "IMG_1.HEIC"), DateLayout.Map("DCIM\\100APPLE\\IMG_1.HEIC", new DateTime(1970, 1, 1)));
    }

    [Fact]
    public void Works_on_any_folder_without_manifest_or_iphone_and_creates_no_manifest()
    {
        using var sb = new Sandbox();
        var folder = Path.Combine(sb.Root, "mis-fotos");
        // Estructura caótica, copiada "a mano": nada de .iphotos ni de iPhone.
        Directory.CreateDirectory(Path.Combine(folder, "vacaciones"));
        var a = Path.Combine(folder, "IMG_0001.HEIC");
        var b = Path.Combine(folder, "vacaciones", "IMG_0002.JPG");
        var notMedia = Path.Combine(folder, "apuntes.txt");
        File.WriteAllBytes(a, new byte[100]); File.SetLastWriteTime(a, new DateTime(2023, 8, 5));
        File.WriteAllBytes(b, new byte[200]); File.SetLastWriteTime(b, new DateTime(2022, 1, 20));
        File.WriteAllText(notMedia, "no es una foto");

        var svc = new OrganizeService(new ActivityLog());
        var plan = svc.Plan(folder);
        Assert.Equal(2, plan.Items.Count);

        using var store = FileMover.OpenExistingManifest(folder);
        Assert.Null(store); // no se inventa un manifiesto en una carpeta ajena
        var result = svc.Apply(folder, store, plan, null, default);

        Assert.Equal(2, result.Moved);
        Assert.True(File.Exists(Path.Combine(folder, "2023", "08", "IMG_0001.HEIC")));
        Assert.True(File.Exists(Path.Combine(folder, "2022", "01", "IMG_0002.JPG")));
        Assert.True(File.Exists(notMedia));                                // lo que no es foto/vídeo no se toca
        Assert.False(Directory.Exists(Path.Combine(folder, "vacaciones"))); // la carpeta vacía desaparece
        Assert.False(File.Exists(Path.Combine(folder, ".iphotos", "manifest.db")));
        Assert.Empty(svc.Plan(folder).Items); // repetir no hace nada
    }

    [Fact]
    public async Task Plan_is_a_simulation_and_touches_nothing()
    {
        using var c = new Ctx(Library);
        await c.Download();
        var before = c.Sb.DestFiles().OrderBy(p => p).ToList();

        var plan = c.Org.Plan(c.Sb.Dest);

        Assert.Equal(4, plan.Items.Count);
        Assert.Equal(before, c.Sb.DestFiles().OrderBy(p => p).ToList());
    }

    [Fact]
    public async Task Organize_moves_files_keeps_content_and_manifest_consistent()
    {
        using var c = new Ctx(Library);
        await c.Download();
        var hash1 = Sandbox.Sha(c.Sb.DestFile("DCIM\\100APPLE\\IMG_0001.HEIC"));
        var hash3 = Sandbox.Sha(c.Sb.DestFile("DCIM\\101APPLE\\IMG_0003.HEIC"));

        using (var store = FileMover.OpenExistingManifest(c.Sb.Dest))
        {
            Assert.NotNull(store); // la carpeta es de la app: se mantiene su manifiesto
            var plan = c.Org.Plan(c.Sb.Dest);
            var result = c.Org.Apply(c.Sb.Dest, store, plan, null, default);
            Assert.Equal(4, result.Moved);
            Assert.Empty(result.Failed);
            Assert.NotNull(result.ReportPath);
        }

        Assert.True(File.Exists(c.Sb.DestFile("2024\\01\\IMG_0001.HEIC")));
        Assert.True(File.Exists(c.Sb.DestFile("2024\\03\\IMG_0002.HEIC")));
        Assert.True(File.Exists(c.Sb.DestFile("2025\\12\\IMG_0003.HEIC")));
        Assert.True(File.Exists(c.Sb.DestFile("2025\\12\\IMG_0004.HEIC")));
        Assert.Equal(hash1, Sandbox.Sha(c.Sb.DestFile("2024\\01\\IMG_0001.HEIC")));
        Assert.Equal(hash3, Sandbox.Sha(c.Sb.DestFile("2025\\12\\IMG_0003.HEIC")));
        Assert.False(Directory.Exists(c.Sb.DestFile("DCIM"))); // las carpetas vacías desaparecen

        // La verificación sigue encontrando todo y un nuevo plan ya no tiene nada que hacer.
        using (var store2 = c.Store())
        {
            var v = await new VerificationService(c.Log).VerifyLocalAsync(store2, c.Sb.Dest, true, null, default);
            Assert.Equal(4, v.Verified);
            Assert.Equal(0, v.MissingLocal);
        }
        Assert.Empty(c.Org.Plan(c.Sb.Dest).Items);

        // Y descargar otra vez no recopia nada: la identidad no depende de la carpeta local.
        var again = await c.Download();
        Assert.Equal(0, again.Copied);
        Assert.Equal(4, again.AlreadyDone);
    }

    [Fact]
    public async Task Manifest_is_repaired_when_a_previous_run_moved_a_file_without_saving_it()
    {
        using var c = new Ctx(Library);
        await c.Download();

        // Simula un corte justo entre mover el archivo y anotarlo en el manifiesto.
        Directory.CreateDirectory(c.Sb.DestFile("2024\\01"));
        File.Move(c.Sb.DestFile("DCIM\\100APPLE\\IMG_0001.HEIC"), c.Sb.DestFile("2024\\01\\IMG_0001.HEIC"));

        MoveResult result;
        using (var store = FileMover.OpenExistingManifest(c.Sb.Dest))
        {
            var plan = c.Org.Plan(c.Sb.Dest);
            Assert.Equal(3, plan.Items.Count); // el movido ya está en su sitio
            result = c.Org.Apply(c.Sb.Dest, store, plan, null, default);
        }
        Assert.Equal(1, result.Reconciled);
        Assert.Equal(3, result.Moved);

        using var store2 = c.Store();
        var v = await new VerificationService(c.Log).VerifyLocalAsync(store2, c.Sb.Dest, true, null, default);
        Assert.Equal(0, v.MissingLocal);
        Assert.Equal(4, v.Verified);
    }

    [Fact]
    public async Task Download_with_organize_option_saves_straight_into_year_month()
    {
        using var c = new Ctx(Library);
        var first = await c.Download(organize: true);

        Assert.Equal(4, first.Copied);
        Assert.True(File.Exists(c.Sb.DestFile("2024\\01\\IMG_0001.HEIC")));
        Assert.True(File.Exists(c.Sb.DestFile("2025\\12\\IMG_0003.HEIC")));
        Assert.False(Directory.Exists(c.Sb.DestFile("DCIM")));

        var second = await c.Download(organize: true);
        Assert.Equal(0, second.Copied);
    }

    [Fact]
    public async Task Organize_never_overwrites_a_different_file_with_the_same_name()
    {
        using var c = new Ctx(Library);
        await c.Download();
        // Ya hay OTRO archivo con el mismo nombre en el destino final.
        Directory.CreateDirectory(c.Sb.DestFile("2024\\01"));
        File.WriteAllText(c.Sb.DestFile("2024\\01\\IMG_0001.HEIC"), "otro contenido");
        File.SetLastWriteTime(c.Sb.DestFile("2024\\01\\IMG_0001.HEIC"), new DateTime(2024, 1, 2)); // ya está en su carpeta

        using (var store = FileMover.OpenExistingManifest(c.Sb.Dest))
            c.Org.Apply(c.Sb.Dest, store, c.Org.Plan(c.Sb.Dest), null, default);

        Assert.Equal("otro contenido", File.ReadAllText(c.Sb.DestFile("2024\\01\\IMG_0001.HEIC")));
        Assert.True(File.Exists(c.Sb.DestFile("2024\\01\\IMG_0001 (2).HEIC")));
        using var store2 = c.Store();
        var v = await new VerificationService(c.Log).VerifyLocalAsync(store2, c.Sb.Dest, true, null, default);
        Assert.Equal(0, v.MissingLocal);
    }

    // ------------------------------------------------------------------ Live Photos (carpeta cualquiera)

    [Fact]
    public void LivePhotos_work_on_any_folder_and_only_pair_a_photo_with_its_own_clip()
    {
        using var sb = new Sandbox();
        LiveLibrary(sb);
        var folder = sb.Source; // una carpeta cualquiera, sin manifiesto
        var svc = new LivePhotoService(new ActivityLog());

        var plan = svc.Plan(folder);

        var item = Assert.Single(plan.Items);
        Assert.Equal("DCIM\\100APPLE\\IMG_0001.MOV", item.From);
        Assert.Equal(Clip(LivePhotoService.BackupFolder, "DCIM", "100APPLE", "IMG_0001.MOV"), item.To);

        var result = svc.Apply(folder, null, plan, null, default);
        Assert.Equal(1, result.Moved);
        Assert.True(File.Exists(Path.Combine(folder, LivePhotoService.BackupFolder, "DCIM", "100APPLE", "IMG_0001.MOV")));
        Assert.True(File.Exists(Path.Combine(folder, "DCIM", "100APPLE", "IMG_0002.MOV"))); // vídeo normal: intacto
        Assert.True(File.Exists(Path.Combine(folder, "DCIM", "100APPLE", "IMG_0004.MOV"))); // fechas distintas: no es pareja
        Assert.Empty(svc.Plan(folder).Items);
        Assert.Equal(1, svc.Plan(folder).AlreadyInPlace);
    }

    [Fact]
    public async Task LivePhotos_apply_sets_the_clip_aside_without_deleting_and_keeps_everything_verifiable()
    {
        using var c = new Ctx(LiveLibrary);
        await c.Download();
        var clipHash = Sandbox.Sha(c.Sb.DestFile("DCIM\\100APPLE\\IMG_0001.MOV"));

        MoveResult result;
        using (var store = FileMover.OpenExistingManifest(c.Sb.Dest))
        {
            result = c.Live.Apply(c.Sb.Dest, store, c.Live.Plan(c.Sb.Dest), null, default);

            var v = await new VerificationService(c.Log).VerifyLocalAsync(store!, c.Sb.Dest, true, null, default);
            Assert.Equal(6, v.Verified);
            Assert.Equal(0, v.MissingLocal);
        }
        Assert.Empty(c.Live.Plan(c.Sb.Dest).Items); // repetir no hace nada

        Assert.Equal(1, result.Moved);
        var moved = c.Sb.DestFile(Clip(LivePhotoService.BackupFolder, "DCIM", "100APPLE", "IMG_0001.MOV"));
        Assert.True(File.Exists(moved));
        Assert.Equal(clipHash, Sandbox.Sha(moved));
        Assert.False(File.Exists(c.Sb.DestFile("DCIM\\100APPLE\\IMG_0001.MOV")));
        Assert.True(File.Exists(c.Sb.DestFile("DCIM\\100APPLE\\IMG_0001.HEIC")));

        // Descargar otra vez no recopia el clip apartado.
        Assert.Equal(0, (await c.Download()).Copied);
    }

    [Fact]
    public void Organize_keeps_a_live_photo_and_its_clip_together_even_across_a_month_boundary()
    {
        using var sb = new Sandbox();
        var folder = sb.Source;
        Photo(sb, "IMG_0001.HEIC", 1, new DateTime(2024, 3, 31, 23, 59, 59));
        Photo(sb, "IMG_0001.MOV", 2, new DateTime(2024, 4, 1, 0, 0, 1));

        var svc = new OrganizeService(new ActivityLog());
        svc.Apply(folder, null, svc.Plan(folder), null, default);

        Assert.True(File.Exists(Path.Combine(folder, "2024", "03", "IMG_0001.HEIC")));
        Assert.True(File.Exists(Path.Combine(folder, "2024", "03", "IMG_0001.MOV")));
    }

    [Fact]
    public void Organize_leaves_already_set_aside_clips_where_they_are()
    {
        using var sb = new Sandbox();
        LiveLibrary(sb);
        var folder = sb.Source;
        var live = new LivePhotoService(new ActivityLog());
        live.Apply(folder, null, live.Plan(folder), null, default);

        var org = new OrganizeService(new ActivityLog());
        org.Apply(folder, null, org.Plan(folder), null, default);

        Assert.True(File.Exists(Path.Combine(folder, LivePhotoService.BackupFolder, "DCIM", "100APPLE", "IMG_0001.MOV")));
        Assert.True(File.Exists(Path.Combine(folder, "2024", "05", "IMG_0001.HEIC")));
        Assert.False(File.Exists(Path.Combine(folder, "2024", "05", "IMG_0001.MOV")));
    }

    [Fact]
    // Antes comprobaba el plan de borrado (retirado en v1.5); protege lo mismo: lo movido se sigue reconociendo como copiado.
    public async Task Moved_files_are_still_recognised_as_copied_and_verified()
    {
        using var c = new Ctx(Library);
        await c.Download();
        using (var store = FileMover.OpenExistingManifest(c.Sb.Dest))
            c.Org.Apply(c.Sb.Dest, store, c.Org.Plan(c.Sb.Dest), null, default);

        using var store2 = c.Store();
        var verify = await new VerificationService(c.Log).VerifyLocalAsync(store2, c.Sb.Dest, true, null, default);
        Assert.Equal(4, verify.Verified);

        using var backend = c.Provider.Open(c.Device);
        var scan = new TransferEngine(c.Log).Scan(backend, store2, c.Options(), null, default);
        Assert.Equal(4, scan.AlreadyDone);
        Assert.Empty(scan.Pending);
    }
}
