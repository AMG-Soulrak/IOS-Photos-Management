using System.Globalization;

namespace IPhonePhotos.Core.Organize;

/// <summary>Una foto del álbum: su archivo, cuándo se hizo y (si hay GPS y lista de lugares) dónde.</summary>
/// <param name="LiveClip">Ruta relativa del clip .MOV si es un Live Photo (esté junto a la foto o apartado en la carpeta de respaldo).</param>
public sealed record AlbumItem(LocalMedia Media, DateTime Date, double? Lat, double? Lon, string? Place, string? LiveClip = null);

public sealed record AlbumGroup(string Name, List<AlbumItem> Items);

public sealed record AlbumData(List<AlbumItem> Items, bool HasPlaceNames, int VideosAndOthers);

/// <summary>Carga una carpeta de fotos como álbum y la agrupa por fecha o por lugar. No toca ningún archivo.</summary>
public static class Album
{
    public const string UnknownPlace = "Sin ubicación";
    public const string FarPlace = "Otros lugares";
    private static readonly CultureInfo Es = new("es-ES");

    /// <summary>Identifica una foto aunque cambie de carpeta (nombre, tamaño y fecha): sirve de clave en las cachés de caras, huellas y metadatos.</summary>
    public static string Key(LocalMedia f) => $"{Path.GetFileName(f.RelativePath)}|{f.Size}|{f.Date.Ticks}";

    public static AlbumData Load(string root, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var all = MediaScanner.Scan(root, ct, includeAlbums: true);
        var stills = all.Where(f => PhotoInfoReader.IsStill(f.RelativePath)).ToList();
        var info = PhotoInfoReader.ReadAll(root, stills, progress, ct);
        var finder = PlaceFinder.Load(root);

        var clipOf = LivePhotoService.FindPairs(all).ToDictionary(p => p.Photo.RelativePath, p => p.Video.RelativePath, StringComparer.OrdinalIgnoreCase);
        var setAside = SetAsideClips(root);

        var items = stills.Select(f =>
        {
            info.TryGetValue(f.RelativePath, out var i);
            string? place = null;
            if (i is { Lat: { } la, Lon: { } lo })
                place = finder == null ? null : finder.Find(la, lo) ?? FarPlace;
            // Un clip apartado por la limpieza de Live Photos ya no está junto a su foto: se reconoce por nombre y fecha.
            var clip = clipOf.GetValueOrDefault(f.RelativePath)
                       ?? (setAside.TryGetValue(f.Stem.ToLowerInvariant(), out var c) && (c.Date - f.Date).Duration() <= LivePhotoService.PairTolerance ? c.Rel : null);
            return new AlbumItem(f, i?.Taken ?? f.Date, i?.Lat, i?.Lon, place, clip);
        }).ToList();
        return new AlbumData(items, finder != null, all.Count - stills.Count);
    }

    private static Dictionary<string, (string Rel, DateTime Date)> SetAsideClips(string root)
    {
        var map = new Dictionary<string, (string, DateTime)>();
        var dir = Path.Combine(root, LivePhotoService.BackupFolder);
        if (!Directory.Exists(dir)) return map;
        foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*.mov", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            map[Path.GetFileNameWithoutExtension(fi.Name).ToLowerInvariant()] = (Path.GetRelativePath(root, fi.FullName), fi.LastWriteTime);
        return map;
    }

    /// <summary>Las fotos que están dentro del álbum propio <paramref name="album"/> (carpeta «Álbumes\nombre»).</summary>
    public static List<AlbumItem> InAlbum(IEnumerable<AlbumItem> items, string album)
        => InFolder(items, Path.Combine(AlbumService.Folder, album));

    /// <summary>Las fotos que están dentro de una carpeta de la biblioteca (por ejemplo «Nuevos»).</summary>
    public static List<AlbumItem> InFolder(IEnumerable<AlbumItem> items, string relativeFolder)
    {
        var prefix = relativeFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return items.Where(i => i.Media.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Date).ToList();
    }

    /// <summary>Más reciente primero; el nombre del grupo es el mes («marzo de 2024»).</summary>
    public static List<AlbumGroup> ByDate(IEnumerable<AlbumItem> items)
        => items.GroupBy(i => (i.Date.Year, i.Date.Month))
            .OrderByDescending(g => g.Key)
            .Select(g => new AlbumGroup(new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM 'de' yyyy", Es),
                g.OrderBy(i => i.Date).ToList()))
            .ToList();

    /// <summary>Los lugares con más fotos primero; lo que no tiene GPS va al final.</summary>
    public static List<AlbumGroup> ByPlace(IEnumerable<AlbumItem> items)
        => items.GroupBy(i => i.Place ?? UnknownPlace)
            .OrderBy(g => g.Key == UnknownPlace || g.Key == FarPlace ? 1 : 0)
            .ThenByDescending(g => g.Count())
            .Select(g => new AlbumGroup(g.Key, g.OrderBy(i => i.Date).ToList()))
            .ToList();
}
