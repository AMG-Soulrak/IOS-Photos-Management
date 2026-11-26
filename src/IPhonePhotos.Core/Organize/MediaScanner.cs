using System.IO.Enumeration;

namespace IPhonePhotos.Core.Organize;

/// <summary>Un archivo multimedia que ya está en el PC. La fecha es la del archivo (la que ve el Explorador).</summary>
public sealed record LocalMedia(string RelativePath, long Size, DateTime Date)
{
    public string Directory => Path.GetDirectoryName(RelativePath) ?? "";
    public string Stem => Path.GetFileNameWithoutExtension(RelativePath);
    public string Extension => Path.GetExtension(RelativePath).ToLowerInvariant();
}

/// <summary>
/// Recorre una carpeta cualquiera con fotos y vídeos ya descargados (no hace falta que la haya creado esta aplicación
/// ni que haya un iPhone conectado). Ignora la carpeta de control (.iphotos) y la de clips de Live Photos apartados.
/// </summary>
public static class MediaScanner
{
    private static readonly HashSet<string> Photos = new(StringComparer.OrdinalIgnoreCase)
        { ".heic", ".heif", ".jpg", ".jpeg", ".png", ".gif", ".tif", ".tiff", ".dng", ".bmp", ".webp" };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mov", ".mp4", ".m4v", ".3gp", ".avi" };

    // .AAE: ajustes de edición que el iPhone guarda junto a la foto.
    private static readonly HashSet<string> Sidecars = new(StringComparer.OrdinalIgnoreCase) { ".aae" };

    public static bool IsMedia(string path)
    {
        var ext = Path.GetExtension(path);
        return Photos.Contains(ext) || Videos.Contains(ext) || Sidecars.Contains(ext);
    }

    /// <summary>Carpetas de primer nivel que nunca se recorren.</summary>
    private static bool IsReserved(string firstSegment)
        => string.Equals(firstSegment, Manifest.ManifestStore.FolderName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(firstSegment, LivePhotoService.BackupFolder, StringComparison.OrdinalIgnoreCase)
           || string.Equals(firstSegment, DuplicateService.QuarantineFolder, StringComparison.OrdinalIgnoreCase)
           || string.Equals(firstSegment, DiscardService.Folder, StringComparison.OrdinalIgnoreCase)
           || string.Equals(firstSegment, AlbumService.Folder, StringComparison.OrdinalIgnoreCase);

    /// <param name="includeAlbums">Si es false, las fotos que el usuario ha puesto en sus álbumes se ignoran (así «Organizar» no las
    /// saca de su álbum). El álbum, las repetidas y el movimiento a un álbum sí las ven.</param>
    public static List<LocalMedia> Scan(string root, CancellationToken ct = default, bool includeAlbums = false)
    {
        var result = new List<LocalMedia>();
        if (!Directory.Exists(root)) return result;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        // No se entra en carpetas que son enlaces (junction/symlink): llevarían fuera de la biblioteca y «Organizar» movería fotos ajenas.
        // Los archivos con atributo de reparse (p. ej. OneDrive «solo en la nube») sí se listan.
        var files = new FileSystemEnumerable<FileInfo>(root, (ref FileSystemEntry e) => (FileInfo)e.ToFileSystemInfo(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };
        foreach (var fi in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsMedia(fi.Name)) continue;

            var rel = Path.GetRelativePath(root, fi.FullName);
            var first = rel.Split(Path.DirectorySeparatorChar, 2)[0];
            if (rel.Contains(Path.DirectorySeparatorChar) && IsReserved(first)
                && !(includeAlbums && string.Equals(first, AlbumService.Folder, StringComparison.OrdinalIgnoreCase))) continue;

            result.Add(new LocalMedia(rel, fi.Length, fi.LastWriteTime));
        }
        return result;
    }

    /// <summary>Cuántos clips hay ya apartados por la limpieza de Live Photos.</summary>
    public static int CountSetAside(string root)
    {
        var folder = Path.Combine(root, LivePhotoService.BackupFolder);
        if (!Directory.Exists(folder)) return 0;
        return new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Count(f => IsMedia(f.Name));
    }
}
