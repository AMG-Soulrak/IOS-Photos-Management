using System.Security.Cryptography;
using System.Text;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Transfer;

namespace IPhonePhotos.Core.Organize;

/// <summary>Un archivo que se va a mover (rutas relativas a la carpeta elegida).</summary>
public sealed record MoveItem(string From, string To, long Size);

/// <summary>Resultado de planificar un movimiento de archivos: qué se movería y qué se deja como está (con el motivo).</summary>
public sealed class MovePlan
{
    public List<MoveItem> Items { get; } = new();
    public int AlreadyInPlace { get; set; }
    public long TotalBytes => Items.Sum(i => i.Size);

    /// <summary>Avisos y archivos que se dejan sin tocar, con el motivo.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Archivos que había en la carpeta al calcular el plan (se usa para reparar el manifiesto tras un corte).</summary>
    internal List<LocalMedia> Scanned { get; set; } = new();
}

public sealed class MoveResult
{
    public int Moved { get; set; }

    /// <summary>Registros del manifiesto corregidos porque un corte anterior movió el archivo sin anotarlo.</summary>
    public int Reconciled { get; set; }

    public long BytesMoved { get; set; }
    public List<string> Failed { get; } = new();
    public string? ReportPath { get; set; }
}

/// <summary>
/// Mueve archivos YA DESCARGADOS dentro de la carpeta elegida. Funciona sobre cualquier carpeta: si además contiene el
/// manifiesto de esta aplicación (.iphotos\manifest.db) lo mantiene al día (<see cref="FileRecord.LocalRelativePath"/>),
/// de modo que la verificación y el borrado seguro del iPhone siguen encontrando cada archivo.
/// Nunca sobrescribe y nunca borra contenido: solo cambia de carpeta.
/// </summary>
public static class FileMover
{
    public const string ReportsFolder = "reports";

    /// <summary>Abre el manifiesto de la carpeta SOLO si ya existe (no crea nada en carpetas ajenas). Null si no hay.</summary>
    public static ManifestStore? OpenExistingManifest(string root)
    {
        var db = Path.Combine(ManifestStore.MetaFolder(root), "manifest.db");
        if (!File.Exists(db)) return null;
        try { return ManifestStore.Open(root); }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "La carpeta está siendo usada por otra operación (¿hay una descarga en curso?). Espera a que termine e inténtalo de nuevo.", ex);
        }
    }

    /// <summary>Mueve un archivo sin sobrescribir. Devuelve la ruta relativa final (cambia a "nombre (2)" si ya existe otro igual).</summary>
    public static string Move(string root, string from, string to)
    {
        var src = Path.Combine(root, from);
        var dst = Path.Combine(root, to);
        if (!File.Exists(src)) throw new FileNotFoundException($"No existe: {from}");

        if (File.Exists(dst))
        {
            to = TransferEngine.ResolveFreeRelativePath(root, to);
            dst = Path.Combine(root, to);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Move(src, dst, overwrite: false);
        return to;
    }

    /// <summary>
    /// Si un corte anterior movió un archivo sin llegar a anotarlo en el manifiesto, el registro apunta a una ruta que ya
    /// no existe. Se busca el archivo (mismo nombre y tamaño, y mismo contenido si se conoce su hash) y se corrige.
    /// </summary>
    public static int Reconcile(ManifestStore store, string root, IReadOnlyList<LocalMedia> files, ActivityLog log)
    {
        var records = store.All().Where(r => r.IsCopied).ToList();
        var claimed = new HashSet<string>(
            records.Where(r => File.Exists(Path.Combine(root, r.EffectiveLocalRelativePath))).Select(r => r.EffectiveLocalRelativePath),
            StringComparer.OrdinalIgnoreCase);

        var fixedCount = 0;
        foreach (var rec in records)
        {
            var current = rec.EffectiveLocalRelativePath;
            if (File.Exists(Path.Combine(root, current))) continue;

            var name = Path.GetFileName(current);
            var candidates = files
                .Where(f => !claimed.Contains(f.RelativePath)
                            && f.Size == rec.Size
                            && string.Equals(Path.GetFileName(f.RelativePath), name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count != 1) continue;

            var candidate = candidates[0];
            if (rec.Sha256 != null && !string.Equals(HashOf(Path.Combine(root, candidate.RelativePath)), rec.Sha256, StringComparison.Ordinal))
                continue;

            rec.LocalRelativePath = candidate.RelativePath;
            store.Upsert(rec);
            claimed.Add(candidate.RelativePath);
            fixedCount++;
            log.Info($"Manifiesto corregido: {name} estaba en {current} y ahora está en {candidate.RelativePath}.");
        }
        return fixedCount;
    }

    private static string HashOf(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Ejecuta un plan: un archivo cada vez, anotando el manifiesto (si hay) tras cada movimiento.</summary>
    public static MoveResult Apply(string root, ManifestStore? store, MovePlan plan, ActivityLog log,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var result = new MoveResult();
        if (store != null) result.Reconciled = Reconcile(store, root, plan.Scanned, log);

        // Índice "ruta local -> registro" para anotar cada movimiento sin recorrer el manifiesto entero cada vez.
        var byLocalPath = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        if (store != null)
            foreach (var r in store.All())
                byLocalPath.TryAdd(r.EffectiveLocalRelativePath, r);

        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytesDone = 0;
        var done = 0;

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new TransferProgress(TransferPhase.Organizing, done, plan.Items.Count, bytesDone, plan.TotalBytes,
                item.From, 0, item.Size, 0));

            try
            {
                var finalRel = Move(root, item.From, item.To);
                result.Moved++;
                result.BytesMoved += item.Size;
                var dir = Path.GetDirectoryName(item.From);
                if (!string.IsNullOrEmpty(dir)) touchedDirs.Add(dir);

                if (byLocalPath.TryGetValue(item.From, out var rec))
                {
                    rec.LocalRelativePath = finalRel;
                    store!.Upsert(rec);
                    byLocalPath.Remove(item.From);
                    byLocalPath[finalRel] = rec;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failed.Add($"{item.From}: {ex.Message}");
                log.Warn($"No se pudo mover {item.From}: {ex.Message}");
            }

            done++;
            bytesDone += item.Size;
        }

        foreach (var dir in touchedDirs) RemoveEmptyFolders(root, dir);
        progress?.Report(new TransferProgress(TransferPhase.Done, done, plan.Items.Count, bytesDone, plan.TotalBytes, null, 0, 0, 0));
        return result;
    }

    /// <summary>Quita carpetas que se han quedado vacías (sube hasta la carpeta elegida, sin tocarla).</summary>
    private static void RemoveEmptyFolders(string root, string relativeDir)
    {
        var current = relativeDir;
        while (!string.IsNullOrEmpty(current))
        {
            var full = Path.Combine(root, current);
            try
            {
                if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any()) return;
                Directory.Delete(full);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            current = Path.GetDirectoryName(current) ?? "";
        }
    }

    /// <summary>Guarda un informe de texto en .iphotos\reports\. Devuelve la ruta (o null si no se pudo escribir).</summary>
    public static string? WriteReport(string root, string prefix, string title, MovePlan plan, MoveResult? result)
    {
        try
        {
            var folder = Path.Combine(ManifestStore.MetaFolder(root), ReportsFolder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine($"Carpeta: {root}");
            sb.AppendLine($"Fecha: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Modo: {(result == null ? "SIMULACIÓN (no se movió nada)" : "APLICADO")}");
            sb.AppendLine($"Archivos a mover: {plan.Items.Count} ({Format.Bytes(plan.TotalBytes)}). Ya en su sitio: {plan.AlreadyInPlace}.");
            if (result != null)
                sb.AppendLine($"Movidos: {result.Moved}. Registros corregidos: {result.Reconciled}. Fallidos: {result.Failed.Count}.");
            sb.AppendLine();
            foreach (var n in plan.Notes) sb.AppendLine($"[nota] {n}");
            if (result != null) foreach (var f in result.Failed) sb.AppendLine($"[ERROR] {f}");
            sb.AppendLine();
            foreach (var i in plan.Items) sb.AppendLine($"{i.From}  ->  {i.To}");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
