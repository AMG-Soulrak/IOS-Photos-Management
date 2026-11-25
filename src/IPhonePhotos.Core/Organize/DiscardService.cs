using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Transfer;

namespace IPhonePhotos.Core.Organize;

public enum DiscardKind { Screenshot, Document }

/// <summary>Una foto que probablemente sobra (captura de pantalla o foto de un papel/ticket) y por qué se sospecha.</summary>
public sealed record DiscardCandidate(AlbumItem Item, DiscardKind Kind, string Reason);

/// <summary>
/// Busca fotos «fuera de contexto». Las CAPTURAS de pantalla se reconocen con bastante seguridad: el iPhone las guarda como PNG
/// y la cámara nunca lo hace. Los DOCUMENTOS (tickets, facturas, folios) se adivinan por cómo es la imagen: mucho fondo claro,
/// casi sin color y con trazos oscuros de texto. No se apartan solas: la persona decide.
/// shortcut: sin un modelo de visión no se distinguen las fotos hechas a una pantalla, a una pizarra o a un cartel, y un
/// documento sobre fondo oscuro o de color pasa desapercibido; algún cuadro claro y sin color (nieve, un folio-dibujo) puede
/// salir de más. Para afinarlo habría que clasificar con un modelo (p. ej. CLIP), que son cientos de MB.
/// </summary>
public sealed class DiscardService
{
    public const string Folder = "_Descartadas";
    private const int Version = 1;
    private readonly ActivityLog _log;
    public DiscardService(ActivityLog log) => _log = log;

    private sealed record DocStats(double Light, double Dark, double Saturation, int Version = Version);

    public static List<DiscardCandidate> Find(string root, IReadOnlyList<AlbumItem> items, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var found = new ConcurrentBag<DiscardCandidate>();
        var rest = new List<AlbumItem>();
        foreach (var i in items)
        {
            if (IsScreenshot(i)) found.Add(new DiscardCandidate(i, DiscardKind.Screenshot, "Captura de pantalla (PNG, sin datos de cámara)"));
            else if (i.LiveClip == null) rest.Add(i); // un Live Photo es de la cámara: no es un papel
        }

        var cachePath = Path.Combine(ManifestStore.MetaFolder(root), "discard.json");
        var cache = new ConcurrentDictionary<string, DocStats>();
        try
        {
            if (File.Exists(cachePath))
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, DocStats>>(File.ReadAllText(cachePath)) ?? new())
                    if (kv.Value.Version == Version) cache[kv.Key] = kv.Value;
        }
        catch { /* caché ilegible: se recalcula */ }

        var done = 0;
        Parallel.ForEach(rest, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, item =>
        {
            var key = Album.Key(item.Media);
            var stats = cache.GetOrAdd(key, _ => Measure(Path.Combine(root, item.Media.RelativePath)) ?? new DocStats(0, 0, 1));
            if (IsDocument(stats))
                found.Add(new DiscardCandidate(item, DiscardKind.Document, $"Parece un documento o ticket ({stats.Light:P0} fondo claro, casi sin color)"));
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0) progress?.Report((n, rest.Count));
        });
        progress?.Report((rest.Count, rest.Count));
        try { File.WriteAllText(cachePath, JsonSerializer.Serialize(cache.ToDictionary(k => k.Key, k => k.Value))); } catch { /* solo es caché */ }
        return found.OrderBy(c => c.Kind).ThenByDescending(c => c.Item.Date).ToList();
    }

    public static bool IsScreenshot(AlbumItem i)
        => Path.GetExtension(i.Media.RelativePath).Equals(".png", StringComparison.OrdinalIgnoreCase) && i.Lat == null;

    private static bool IsDocument(DocStats s) => s.Light >= 0.55 && s.Dark is >= 0.02 and <= 0.30 && s.Saturation < 0.15;

    // Miniatura de 128 px: % de píxeles claros, % de oscuros (el texto) y color medio.
    private static DocStats? Measure(string path)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path);
            b.DecodePixelWidth = 128;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.EndInit();
            var bgr = new FormatConvertedBitmap(b, PixelFormats.Bgr24, null, 0);
            int w = bgr.PixelWidth, h = bgr.PixelHeight, stride = w * 3;
            var px = new byte[stride * h];
            bgr.CopyPixels(px, stride, 0);
            int light = 0, dark = 0, n = w * h;
            double sat = 0;
            for (var i = 0; i < px.Length; i += 3)
            {
                int bl = px[i], g = px[i + 1], r = px[i + 2];
                var luma = (0.299 * r) + (0.587 * g) + (0.114 * bl);
                var max = Math.Max(r, Math.Max(g, bl));
                var min = Math.Min(r, Math.Min(g, bl));
                if (luma > 185) light++;
                else if (luma < 90) dark++;
                sat += max == 0 ? 0 : (max - min) / (double)max;
            }
            return new DocStats((double)light / n, (double)dark / n, sat / n);
        }
        catch { return null; } // no se puede leer: no se sospecha de ella
    }

    /// <summary>Aparta a <see cref="Folder"/> (con la misma ruta relativa) las fotos indicadas; no borra nada.</summary>
    public MoveResult SetAside(string root, ManifestStore? store, IEnumerable<AlbumItem> items, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var albums = AlbumService.List(root);
        var plan = new MovePlan { Scanned = MediaScanner.Scan(root, ct, includeAlbums: true) };
        var size = plan.Scanned.ToDictionary(f => f.RelativePath, f => f.Size, StringComparer.OrdinalIgnoreCase);
        foreach (var rel in items.SelectMany(i => new[] { i.Media.RelativePath, i.LiveClip }).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
            plan.Items.Add(new MoveItem(rel!, Path.Combine(Folder, rel!), size.GetValueOrDefault(rel!)));
        var result = FileMover.Apply(root, store, plan, _log, progress, ct);
        AlbumService.EnsureFolders(root, albums);
        result.ReportPath = FileMover.WriteReport(root, "descartadas", "Fotos descartadas apartadas a " + Folder, plan, result);
        _log.Info($"Descartadas: {result.Moved} archivos apartados a {Folder}, {result.Failed.Count} fallidos.");
        return result;
    }
}
