using IPhonePhotos.Core.Models;
using LiteDB;

namespace IPhonePhotos.Core.Manifest;

/// <summary>
/// Manifiesto de reanudación: una base de datos de un solo archivo (LiteDB, 100 % .NET, sin librerías nativas)
/// guardada DENTRO de la carpeta de destino (.iphotos\manifest.db). Así viaja con las fotos.
///
/// Es único por carpeta de destino y NO depende de la técnica con la que se copió: así, si una copia empieza
/// por WPD y continúa por AFC (o por una copia de seguridad), sabe exactamente qué falta. Cada archivo se
/// reconoce por ruta + tamaño + fecha (ver <see cref="FileIdentity"/>), no por el dispositivo.
/// Cada cambio de estado se guarda al momento, de modo que un corte en cualquier punto es recuperable.
/// </summary>
public sealed class ManifestStore : IDisposable
{
    public const string FolderName = ".iphotos";

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<FileRecord> _files;

    private readonly string _root;

    private ManifestStore(LiteDatabase db, string path, string root)
    {
        _db = db;
        _root = root;
        DatabasePath = path;
        _files = db.GetCollection<FileRecord>("files");
        _files.EnsureIndex(x => x.RelativePath);
        _files.EnsureIndex(x => x.State);
    }

    public string DatabasePath { get; }

    public static string MetaFolder(string destinationRoot) => Path.Combine(destinationRoot, FolderName);

    public static ManifestStore Open(string destinationRoot)
    {
        var folder = MetaFolder(destinationRoot);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "manifest.db");
        var store = new ManifestStore(OpenDb(path), path, destinationRoot);
        if (!store.LegacyRecords().Any()) return store;

        // Manifiesto de ≤ v1.4 con estados que ya no valen: copia previa del fichero (con la base cerrada) y migración.
        store.Dispose();
        var backup = $"{path}.bak-{DateTime.Now:yyyyMMdd}";
        for (var n = 2; File.Exists(backup); n++) backup = $"{path}.bak-{DateTime.Now:yyyyMMdd}-{n}";
        File.Copy(path, backup);
        store = new ManifestStore(OpenDb(path), path, destinationRoot);
        store.MigrateLegacy();
        return store;
    }

    private static LiteDatabase OpenDb(string path) =>
        new(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });

    // Notas que ponía la adopción rápida en ≤ v1.4 (sin comparar contenido con el iPhone).
    private const string OldFastNote1 = "adoptado (nombre y tamaño)";
    private const string OldFastNote2 = "ya estaba en la biblioteca (nombre, tamaño y fecha)";

#pragma warning disable CS0618 // FileState.Deleted: solo se lee aquí para migrarlo
    private IEnumerable<FileRecord> LegacyRecords() => _files.Find(x =>
        x.State == FileState.Deleted ||
        ((x.State == FileState.Verified || x.State == FileState.Downloaded) && !x.VerifiedDeep &&
         (x.Note == OldFastNote1 || x.Note == OldFastNote2)));

    /// <summary>
    /// · «Deleted» (borrado del iPhone tras verificar) pasa a «Verified»: ya estaba copiada y comprobada, no se vuelve a
    ///   descargar ni a reintentar. Se conserva <see cref="FileRecord.DeletedAtUtc"/> como dato histórico.
    /// · Lo adoptado rápido en ≤ v1.4 (que la verificación local daba por «Verified» sin mirar el iPhone) pasa a
    ///   «adoptado, sin verificar».
    /// </summary>
    private void MigrateLegacy()
    {
        foreach (var r in LegacyRecords().ToList())
        {
            if (r.State == FileState.Deleted)
            {
                r.State = FileState.Verified;
                r.Note = "migrado de v1.4 (constaba borrado del iPhone)";
            }
            else
            {
                r.State = FileState.AdoptedUnverified;
                r.VerifiedAtUtc = null;
            }
            _files.Update(r);
        }
    }
#pragma warning restore CS0618

    public IReadOnlyList<FileRecord> FindByPath(string relativePath)
        => Safe(_files.Find(x => x.RelativePath == relativePath)).ToList();

    public FileRecord? Get(string id) => Safe(new[] { _files.FindById(id) }).FirstOrDefault();

    public void Upsert(FileRecord record) => _files.Upsert(record);

    public IEnumerable<FileRecord> All() => Safe(_files.FindAll());

    public IReadOnlyList<FileRecord> InState(FileState state) => Safe(_files.Find(x => x.State == state)).ToList();

    /// <summary>
    /// manifest.db está en la carpeta de fotos y se puede manipular: al LEER, una ruta local que saldría de la carpeta
    /// se descarta (la copia elige otra dentro) y un registro cuya ruta del dispositivo saldría de ella se ignora.
    /// Así ni la verificación, ni los álbumes, ni el movedor de archivos tocan nada fuera de la carpeta.
    /// </summary>
    private IEnumerable<FileRecord> Safe(IEnumerable<FileRecord?> records)
    {
        foreach (var r in records)
        {
            if (r == null || !SafePath.IsInside(_root, r.RelativePath)) continue;
            if (r.LocalRelativePath != null && !SafePath.IsInside(_root, r.LocalRelativePath)) r.LocalRelativePath = null;
            yield return r;
        }
    }

    public Dictionary<FileState, int> CountByState()
    {
        var result = Enum.GetValues<FileState>().ToDictionary(s => s, _ => 0);
        foreach (var r in _files.FindAll()) result[r.State]++;
        return result;
    }

    public void Dispose() => _db.Dispose();
}
