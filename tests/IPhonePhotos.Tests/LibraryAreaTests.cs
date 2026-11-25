using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Organize;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>
/// Álbumes, descartes y repetidas (áreas congeladas en v1.5: sin funciones nuevas, pero con pruebas). Mueven la única copia
/// de las fotos, así que se comprueba que nunca se pierde ni se borra nada y que el manifiesto sigue al día.
/// </summary>
public class LibraryAreaTests
{
    /// <summary>Imagen sintética: <paramref name="paint"/> decide el color (R,G,B) de cada píxel.</summary>
    private static string Image(string root, string rel, Func<int, int, (byte R, byte G, byte B)> paint, int size = 160, DateTime? date = null)
    {
        var px = new byte[size * size * 3];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var (r, g, b) = paint(x, y);
            var i = (y * size + x) * 3;
            px[i] = b; px[i + 1] = g; px[i + 2] = r;
        }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr24, null, px, size * 3);
        BitmapEncoder enc = rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 95 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path)) enc.Save(fs);
        File.SetLastWriteTime(path, date ?? new DateTime(2024, 3, 10, 12, 0, 0));
        return path;
    }

    private static (byte, byte, byte) Landscape(int x, int y) => ((byte)(x * 1.5), (byte)(80 + y), (byte)(200 - x));

    // ------------------------------------------------------------------ Álbumes

    [Fact]
    public void Album_add_remove_and_rename_move_files_and_keep_the_manifest_in_sync()
    {
        using var sb = new Sandbox();
        var root = sb.Dest;
        Image(root, "DCIM\\100APPLE\\IMG_0001.JPG", Landscape);
        Image(root, "DCIM\\100APPLE\\IMG_0002.JPG", (x, y) => ((byte)y, (byte)x, 90));
        using (var store = ManifestStore.Open(root))
            foreach (var f in Directory.GetFiles(Path.Combine(root, "DCIM", "100APPLE")))
                store.Upsert(new FileRecord { Id = f, RelativePath = Path.GetRelativePath(root, f), Size = new FileInfo(f).Length, State = FileState.Verified });

        var log = new ActivityLog();
        var svc = new AlbumService(log);
        var album = AlbumService.Create(root, "  Viaje: Roma?  ");
        Assert.Equal("Viaje_ Roma_", album); // caracteres no válidos saneados

        var items = Album.Load(root).Items;
        using (var store = ManifestStore.Open(root))
        {
            var added = svc.Add(root, store, album, items);
            Assert.Equal(2, added.Moved);
            Assert.All(store.All(), r => Assert.StartsWith(Path.Combine(AlbumService.Folder, album), r.EffectiveLocalRelativePath));

            var renamed = svc.Rename(root, store, album, "Roma");
            Assert.All(store.All(), r => Assert.StartsWith(Path.Combine(AlbumService.Folder, renamed), r.EffectiveLocalRelativePath));
            Assert.All(store.All(), r => Assert.True(File.Exists(Path.Combine(root, r.EffectiveLocalRelativePath))));

            var inAlbum = Album.InAlbum(Album.Load(root).Items, "Roma");
            var removed = svc.Remove(root, store, inAlbum);
            Assert.Equal(2, removed.Moved);
            Assert.True(File.Exists(Path.Combine(root, "2024", "03", "IMG_0001.JPG")));
        }
        Assert.True(AlbumService.DeleteIfEmpty(root, "Roma"));
        Assert.Equal(2, sb.DestFiles().Count(f => f.EndsWith(".JPG")));
    }

    [Fact]
    public void Album_names_cannot_escape_the_albums_folder()
    {
        using var sb = new Sandbox();
        Assert.Throws<ArgumentException>(() => AlbumService.Create(sb.Dest, ".."));
        var name = AlbumService.Create(sb.Dest, "..\\fuera");
        Assert.True(Directory.Exists(Path.Combine(AlbumService.AlbumsPath(sb.Dest), name)));
        Assert.False(Directory.Exists(Path.Combine(sb.Dest, "fuera")));
    }

    // ------------------------------------------------------------------ Descartes

    [Fact]
    public void Screenshots_and_documents_are_found_and_set_aside_without_deleting()
    {
        using var sb = new Sandbox();
        var root = sb.Dest;
        Image(root, "DCIM\\100APPLE\\IMG_0001.PNG", Landscape);                         // captura: PNG sin GPS
        Image(root, "DCIM\\100APPLE\\IMG_0002.JPG", Landscape);                         // foto normal
        Image(root, "DCIM\\100APPLE\\IMG_0003.JPG", (x, y) => y % 16 < 2 && x is > 20 and < 140 // folio blanco con renglones de texto
            ? ((byte)20, (byte)20, (byte)20) : ((byte)245, (byte)245, (byte)240));

        var items = Album.Load(root).Items;
        var found = DiscardService.Find(root, items, null, default);
        Assert.Contains(found, c => c.Kind == DiscardKind.Screenshot && c.Item.Media.RelativePath.EndsWith("IMG_0001.PNG"));
        Assert.Contains(found, c => c.Kind == DiscardKind.Document && c.Item.Media.RelativePath.EndsWith("IMG_0003.JPG"));
        Assert.DoesNotContain(found, c => c.Item.Media.RelativePath.EndsWith("IMG_0002.JPG"));

        var result = new DiscardService(new ActivityLog()).SetAside(root, null, found.Select(c => c.Item), null, default);
        Assert.Equal(2, result.Moved);
        Assert.True(File.Exists(Path.Combine(root, DiscardService.Folder, "DCIM", "100APPLE", "IMG_0001.PNG")));
        Assert.Equal(3, sb.DestFiles().Count(f => !f.EndsWith(".txt") && !f.EndsWith(".md"))); // apartadas, no borradas
    }

    // ------------------------------------------------------------------ Repetidas

    [Fact]
    public void Identical_photos_form_one_group_and_quarantine_keeps_the_recommended_one()
    {
        using var sb = new Sandbox();
        var root = sb.Dest;
        var a = Image(root, "DCIM\\100APPLE\\IMG_0001.JPG", Landscape);
        File.Copy(a, Path.Combine(root, "DCIM", "100APPLE", "IMG_0001 (2).JPG"));
        Image(root, "DCIM\\100APPLE\\IMG_0009.JPG", (x, y) => ((byte)(x ^ y), (byte)(x * y % 255), (byte)(255 - y))); // distinta

        var items = Album.Load(root).Items;
        var groups = DuplicateService.Find(root, items);
        var g = Assert.Single(groups);
        Assert.Equal(2, g.Photos.Count);

        var others = g.Photos.Where((_, i) => i != g.Recommended).Select(p => p.Item).ToList();
        var result = new DuplicateService(new ActivityLog()).Quarantine(root, null, others, null, default);
        Assert.Equal(1, result.Moved);
        Assert.Single(Directory.GetFiles(Path.Combine(root, DuplicateService.QuarantineFolder), "*.JPG", SearchOption.AllDirectories));
        Assert.Equal(3, sb.DestFiles().Count(f => f.EndsWith(".JPG"))); // nada borrado
    }
}
