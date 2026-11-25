using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Organize;

/// <summary>
/// Álbumes propios: cada álbum es una carpeta real dentro de «Álbumes» en la carpeta de fotos. Meter una foto en un álbum la MUEVE
/// a esa carpeta (con su clip de Live Photo si está a su lado) y anota el cambio en el manifiesto, igual que «Organizar».
/// Quitarla de un álbum la devuelve a su carpeta Año\Mes. Una foto está en un solo sitio (como las carpetas del Explorador):
/// para tenerla en dos álbumes habría que copiarla.
/// «Organizar», la limpieza de Live Photos y la descarga no tocan lo que hay dentro de «Álbumes».
/// </summary>
public sealed class AlbumService
{
    public const string Folder = "Álbumes";

    /// <summary>Donde caen las fotos recién descargadas del iPhone, a la espera de repartirlas en álbumes.</summary>
    public const string InboxFolder = "Nuevos";
    private readonly ActivityLog _log;

    public AlbumService(ActivityLog log) => _log = log;

    public static string AlbumsPath(string root) => Path.Combine(root, Folder);

    public static List<string> List(string root)
        => Directory.Exists(AlbumsPath(root))
            ? Directory.EnumerateDirectories(AlbumsPath(root)).Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList()
            : new List<string>();

    public static string Clean(string name)
        => string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');

    /// <summary>Crea el álbum (la carpeta) y devuelve su nombre ya limpio. Si ya existe, no hace nada más.</summary>
    public static string Create(string root, string name)
    {
        var clean = Clean(name);
        if (clean.Length == 0) throw new ArgumentException("Escribe un nombre para el álbum.");
        Directory.CreateDirectory(Path.Combine(AlbumsPath(root), clean));
        return clean;
    }

    /// <summary>
    /// Cambia el nombre del álbum (renombra su carpeta) y corrige en el manifiesto la ruta de las fotos que contiene.
    /// Devuelve el nombre final, ya limpio. Falla si el nuevo nombre ya es de otro álbum.
    /// </summary>
    public string Rename(string root, ManifestStore? store, string oldName, string newName)
    {
        var clean = Clean(newName);
        if (clean.Length == 0) throw new ArgumentException("Escribe un nombre para el álbum.");
        if (clean == oldName) return clean;
        var from = Path.Combine(AlbumsPath(root), oldName);
        var to = Path.Combine(AlbumsPath(root), clean);
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException($"No existe el álbum «{oldName}».");
        var onlyCase = string.Equals(oldName, clean, StringComparison.OrdinalIgnoreCase);
        if (!onlyCase && Directory.Exists(to)) throw new IOException($"Ya existe un álbum llamado «{clean}».");

        if (onlyCase) // en Windows, cambiar solo mayúsculas/minúsculas exige pasar por un nombre intermedio
        {
            var tmp = from + ".renombrando";
            Directory.Move(from, tmp);
            Directory.Move(tmp, to);
        }
        else Directory.Move(from, to);

        if (store != null)
        {
            var oldPrefix = Path.Combine(Folder, oldName) + Path.DirectorySeparatorChar;
            var newPrefix = Path.Combine(Folder, clean) + Path.DirectorySeparatorChar;
            foreach (var rec in store.All().ToList())
            {
                var path = rec.EffectiveLocalRelativePath;
                if (!path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                rec.LocalRelativePath = newPrefix + path[oldPrefix.Length..];
                store.Upsert(rec);
            }
        }
        _log.Info($"Álbum «{oldName}» renombrado a «{clean}».");
        return clean;
    }

    /// <summary>Borra el álbum solo si está vacío (nunca borra fotos).</summary>
    public static bool DeleteIfEmpty(string root, string album)
    {
        var dir = Path.Combine(AlbumsPath(root), album);
        if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return false;
        Directory.Delete(dir);
        return true;
    }

    /// <summary>Mover el último archivo de una carpeta la borra al limpiar carpetas vacías: los álbumes vacíos se vuelven a crear.</summary>
    public static void EnsureFolders(string root, IEnumerable<string> albums)
    {
        foreach (var a in albums) Directory.CreateDirectory(Path.Combine(AlbumsPath(root), a));
    }

    public MoveResult Add(string root, ManifestStore? store, string album, IEnumerable<AlbumItem> items,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => Move(root, store, items, _ => Path.Combine(Folder, album), album, progress, ct);

    /// <summary>Devuelve las fotos a su carpeta Año\Mes según su fecha.</summary>
    public MoveResult Remove(string root, ManifestStore? store, IEnumerable<AlbumItem> items,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => Move(root, store, items, i => Path.GetDirectoryName(DateLayout.Map(i.Media.RelativePath, i.Date))!, null, progress, ct);

    private MoveResult Move(string root, ManifestStore? store, IEnumerable<AlbumItem> items, Func<AlbumItem, string> destDir,
        string? album, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var albums = List(root);
        var plan = new MovePlan { Scanned = MediaScanner.Scan(root, ct, includeAlbums: true) };
        var size = plan.Scanned.ToDictionary(f => f.RelativePath, f => f.Size, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var dir = destDir(item);
            // El clip de un Live Photo va con su foto, salvo si ya está apartado en su carpeta de respaldo.
            var files = new List<string> { item.Media.RelativePath };
            if (item.LiveClip != null && !item.LiveClip.StartsWith(LivePhotoService.BackupFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                files.Add(item.LiveClip);
            foreach (var rel in files)
            {
                var to = Path.Combine(dir, Path.GetFileName(rel));
                if (!string.Equals(rel, to, StringComparison.OrdinalIgnoreCase))
                    plan.Items.Add(new MoveItem(rel, to, size.GetValueOrDefault(rel)));
            }
        }
        var result = FileMover.Apply(root, store, plan, _log, progress, ct);
        EnsureFolders(root, album == null ? albums : albums.Append(album));
        _log.Info(album != null
            ? $"Álbum «{album}»: {result.Moved} archivos añadidos, {result.Failed.Count} fallidos."
            : $"Álbumes: {result.Moved} archivos devueltos a su carpeta por fecha, {result.Failed.Count} fallidos.");
        return result;
    }
}
