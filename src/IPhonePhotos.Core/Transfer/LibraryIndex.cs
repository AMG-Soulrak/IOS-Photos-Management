using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Organize;

namespace IPhonePhotos.Core.Transfer;

/// <summary>
/// Índice de TODO lo que ya hay en la carpeta de fotos, esté en el álbum, la carpeta por fecha, «Nuevos» o apartado donde esté:
/// (nombre de archivo, tamaño) → rutas. Sirve para reconocer una foto del iPhone que ya se tiene aunque se haya movido o
/// aunque otra técnica la vea con otra ruta (WPD, AFC y copia de seguridad llaman distinto a las carpetas).
/// Una foto del iPhone se da por ya descargada si coincide el nombre, el tamaño y la fecha (esa lectura es barata). Su hash
/// no se puede saber sin descargarla: se guarda al descargarla (o al verificar) y el modo «a fondo» lo compara releyéndola.
/// </summary>
public sealed class LibraryIndex
{
    private readonly Dictionary<(string Name, long Size), List<(string Rel, DateTime Date)>> _map = new();

    public int Count { get; private set; }

    public static LibraryIndex Build(string root, CancellationToken ct)
    {
        var index = new LibraryIndex();
        if (!Directory.Exists(root)) return index;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!MediaScanner.IsMedia(fi.Name)) continue;
            var rel = Path.GetRelativePath(root, fi.FullName);
            if (rel.StartsWith(ManifestStore.FolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var key = (fi.Name.ToLowerInvariant(), fi.Length);
            if (!index._map.TryGetValue(key, out var list)) index._map[key] = list = new();
            list.Add((rel, fi.LastWriteTime));
            index.Count++;
        }
        return index;
    }

    /// <summary>Ruta relativa de un archivo de la biblioteca con el mismo nombre, tamaño y fecha que <paramref name="rf"/>, o null.</summary>
    public string? Find(RemoteFile rf)
    {
        if (rf.Size <= 0) return null;
        var name = Path.GetFileName(rf.RelativePath).ToLowerInvariant();
        if (!_map.TryGetValue((name, rf.Size), out var list)) return null;
        foreach (var (rel, date) in list)
            if (FileIdentity.DatesMatch(date, rf.Modified)) return rel;
        return null;
    }
}
