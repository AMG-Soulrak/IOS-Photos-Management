using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Organize;

/// <summary>
/// Datos de una foto para compararla: huella visual (dHash de 64 bits), tamaño en píxeles y medidas de calidad sacadas de una
/// copia reducida (256 px): nitidez global y de las zonas más nítidas, fracción de píxeles quemados o negros, brillo medio y contraste.
/// </summary>
public sealed record FrameStats(ulong Hash, double Sharpness, int Width, int Height,
    double LocalSharpness = 0, double Clipped = 0, double MeanLuma = 0, double Contrast = 0, int Version = 0);

public sealed record DupPhoto(AlbumItem Item, FrameStats Stats)
{
    public long Size => Item.Media.Size;
    public double Megapixels => Stats.Width * (double)Stats.Height / 1e6;
    /// <summary>Puntuación de calidad 0–100 dentro de su grupo (solo sirve para comparar fotos del mismo grupo).</summary>
    public int Score { get; set; }
    /// <summary>Caras y ojos, si se pudieron analizar (faltan los modelos o la foto no se leyó: null).</summary>
    public FaceStats? Faces { get; set; }
    /// <summary>En qué destaca sobre las otras del grupo, en texto corto («más nítida · mejor exposición»).</summary>
    public string Notes { get; set; } = "";
}

/// <summary>Fotos repetidas o muy parecidas. <see cref="Recommended"/> es la que la app se quedaría; la decisión es del usuario.</summary>
public sealed record DupGroup(List<DupPhoto> Photos, int Recommended);

/// <summary>
/// Detecta fotos iguales o casi iguales (ráfagas, la misma foto guardada dos veces). Una versión editada «IMG_E…» y su original NO cuentan como repetidas
/// y recomienda cuál conservar. No borra nada: las demás se apartan a <see cref="QuarantineFolder"/>, de donde se pueden recuperar.
/// Compara una huella visual (dHash) de cada foto; dos fotos cuentan como parecidas si la huella difiere en pocos bits y se
/// hicieron con poco tiempo de diferencia. La recomendación elige la más nítida (varianza del laplaciano); si hay empate,
/// la de más píxeles y luego la más pesada.
/// shortcut: la nitidez no detecta ojos cerrados ni caras mal salidas, y la ventana de tiempo (<see cref="MaxMinutesApart"/>)
/// deja fuera duplicados con fechas muy distintas; para ellos habría que comparar todas contra todas (mucho más lento).
/// </summary>
public sealed class DuplicateService
{
    public const string QuarantineFolder = "_Duplicados";
    public const int DefaultDistance = 6;
    public const int MaxMinutesApart = 30;
    private const int StatsVersion = 2;

    private readonly ActivityLog _log;
    public DuplicateService(ActivityLog log) => _log = log;

    private static string Key(LocalMedia f) => $"{Path.GetFileName(f.RelativePath)}|{f.Size}|{f.Date.Ticks}";

    public static List<DupGroup> Find(string root, IReadOnlyList<AlbumItem> items, int maxDistance = DefaultDistance,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var stats = ReadAll(root, items, progress, ct);
        var photos = items.Where(i => stats.ContainsKey(i.Media.RelativePath))
            .Select(i => new DupPhoto(i, stats[i.Media.RelativePath])).OrderBy(p => p.Item.Date).ToList();

        // Una foto entra en un grupo solo si se parece a TODAS las que ya tiene (no basta con parecerse a una): así una ráfaga o un paseo
        // con fotos cada vez más distintas no se encadena en un grupo enorme de fotos que no son iguales entre sí.
        var groups = new List<List<DupPhoto>>();
        foreach (var p in photos)
        {
            var home = groups.LastOrDefault(g => (p.Item.Date - g[^1].Item.Date).TotalMinutes <= MaxMinutesApart
                && g.All(q => System.Numerics.BitOperations.PopCount(p.Stats.Hash ^ q.Stats.Hash) <= maxDistance));
            if (home != null) home.Add(p); else groups.Add(new List<DupPhoto> { p });
        }

        // Una versión editada (IMG_E…) y su original no son repetidas: la original sale del grupo (se trata en «Editadas»).
        foreach (var g in groups)
        {
            var editedStems = g.Select(p => (Path.GetDirectoryName(p.Item.Media.RelativePath), EditedVersions.OriginalStem(p.Item.Media.RelativePath)))
                .Where(x => x.Item2 != null).ToHashSet();
            g.RemoveAll(p => editedStems.Contains((Path.GetDirectoryName(p.Item.Media.RelativePath), Path.GetFileNameWithoutExtension(p.Item.Media.RelativePath).ToUpperInvariant())));
        }
        var repeated = groups.Where(g => g.Count > 1).ToList();
        // Las caras y los ojos solo se miran en las fotos que están en un grupo (unos cientos, no toda la biblioteca).
        AnalyzeFaces(root, repeated.SelectMany(g => g).ToList(), progress, ct);

        return repeated
            .Select(list => new DupGroup(list, Recommend(list)))
            .OrderByDescending(g => g.Photos.Count).ThenBy(g => g.Photos[0].Item.Date)
            .ToList();
    }

    // Pesos de la puntuación (suman 0,95; el resto son pequeños extras). Lo que más importa es que no esté movida.
    private const double WSharp = 0.40, WExposure = 0.20, WResolution = 0.15, WContrast = 0.10, WDetail = 0.10, WLive = 0.03, WMeta = 0.02;
    // Solo cuentan en los grupos donde sale alguien: ojos abiertos, caras sin cortar y que salgan todas las personas.
    private const double WEyes = 0.30, WFraming = 0.15, WPeople = 0.15;

    /// <summary>Si es true, en esta ejecución se pudieron cargar los modelos de caras.</summary>
    public static bool FacesAvailable { get; private set; }

    private static void AnalyzeFaces(string root, List<DupPhoto> photos, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        // Las caras son datos biométricos: sin el consentimiento de «Personas» no se analiza nada ni se escribe faces.json.
        if (!PeopleService.IsEnabled) { FacesAvailable = false; return; }
        using var analyzer = FaceAnalyzer.TryCreate();
        FacesAvailable = analyzer != null;
        if (analyzer == null || photos.Count == 0) return;

        var cachePath = Path.Combine(ManifestStore.MetaFolder(root), "faces.json");
        var cache = new ConcurrentDictionary<string, FaceStats?>();
        try
        {
            if (File.Exists(cachePath))
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, FaceStats?>>(File.ReadAllText(cachePath)) ?? new())
                    cache[kv.Key] = kv.Value;
        }
        catch { /* caché ilegible: se vuelve a analizar */ }

        var done = 0;
        var changed = false;
        try
        {
        Parallel.ForEach(photos, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, p =>
        {
            var key = Key(p.Item.Media);
            if (!cache.TryGetValue(key, out var stats) || stats is { Version: < FaceAnalyzer.CurrentVersion })
            {
                stats = analyzer.Analyze(Path.Combine(root, p.Item.Media.RelativePath));
                if (stats != null) { cache[key] = stats; changed = true; }
            }
            p.Faces = stats;
            var n = Interlocked.Increment(ref done);
            if (n % 10 == 0) progress?.Report((n, photos.Count));
        });
        }
        finally // al cancelar se conserva lo ya analizado
        {
        if (changed)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                File.WriteAllText(cachePath, JsonSerializer.Serialize(cache));
            }
            catch { /* sin caché solo es más lento */ }
        }
    }

    /// <summary>
    /// Puntúa cada foto frente al resto de su grupo y devuelve la mejor. Criterios: nitidez (global y de las zonas más nítidas, para
    /// no penalizar un fondo desenfocado a propósito), exposición (pocos píxeles quemados o negros y brillo medio razonable),
    /// resolución, contraste, detalle (peso del archivo) y pequeños extras (conserva su Live Photo, tiene fecha y GPS).
    /// No mira caras ni ojos cerrados ni encuadre. Rellena Score y Notes de cada foto.
    /// </summary>
    private static int Recommend(List<DupPhoto> list)
    {
        double Max(Func<DupPhoto, double> f) => Math.Max(list.Max(f), 1e-9);
        double maxSharp = Max(p => p.Stats.Sharpness), maxLocal = Max(p => p.Stats.LocalSharpness), maxMp = Max(p => p.Megapixels),
               maxContrast = Max(p => p.Stats.Contrast), maxSize = Max(p => p.Size);

        double Exposure(DupPhoto p)
        {
            var lumaPenalty = p.Stats.MeanLuma switch { < 60 => (60 - p.Stats.MeanLuma) / 120, > 200 => (p.Stats.MeanLuma - 200) / 110, _ => 0 };
            return Math.Clamp(1 - p.Stats.Clipped * 3 - lumaPenalty, 0, 1);
        }
        var parts = list.Select(p => new
        {
            Sharp = 0.5 * p.Stats.Sharpness / maxSharp + 0.5 * p.Stats.LocalSharpness / maxLocal,
            Exposure = Exposure(p),
            Resolution = p.Megapixels / maxMp,
            Contrast = p.Stats.Contrast / maxContrast,
            Detail = p.Size / maxSize
        }).ToList();

        // Personas: solo si en alguna foto del grupo sale alguien. Una foto sin analizar cuenta como neutra (no se la penaliza).
        var maxFaces = list.Max(p => p.Faces?.Faces ?? 0);
        var people = maxFaces > 0;
        double Eyes(DupPhoto p) => p.Faces is { OpenEyes: var o, ClosedEyes: var c } && o + c > 0 ? o / (double)(o + c) : 1;
        double Framing(DupPhoto p) => p.Faces is { Faces: > 0 } f ? 1 - f.CutFaces / (double)f.Faces : 1;
        double People(DupPhoto p) => p.Faces == null ? 1 : p.Faces.Faces / (double)maxFaces;

        var scores = list.Select((p, i) => parts[i].Sharp * WSharp + parts[i].Exposure * WExposure + parts[i].Resolution * WResolution
            + parts[i].Contrast * WContrast + parts[i].Detail * WDetail
            + (people ? Eyes(p) * WEyes + Framing(p) * WFraming + People(p) * WPeople : 0)
            + (p.Item.LiveClip != null ? WLive : 0) + (p.Item.Lat != null ? WMeta : 0)).ToList();
        var best = Enumerable.Range(0, list.Count)
            .OrderByDescending(i => Math.Round(scores[i], 3)).ThenByDescending(i => list[i].Stats.Width * (long)list[i].Stats.Height)
            .ThenByDescending(i => list[i].Size).First();

        for (var i = 0; i < list.Count; i++)
        {
            // Frente a la mejor del grupo (que vale 100): si las fotos son casi iguales, las puntuaciones saldrán muy juntas.
            list[i].Score = (int)Math.Round(Math.Min(scores[i] / Math.Max(scores[best], 1e-9), 1) * 100);
            var notes = new List<string>();
            bool Top(Func<int, double> f, double tolerance = 0.98) => f(i) >= Enumerable.Range(0, list.Count).Max(f) * tolerance;
            if (list.Count > 1)
            {
                if (Top(k => parts[k].Sharp)) notes.Add("más nítida");
                if (Top(k => parts[k].Exposure, 0.999) && parts.Any(o => o.Exposure < parts[i].Exposure - 0.05)) notes.Add("mejor exposición");
                if (Top(k => parts[k].Resolution, 0.999) && list.Any(o => o.Megapixels < list[i].Megapixels * 0.99)) notes.Add("más resolución");
                if (Top(k => parts[k].Contrast, 0.97) && parts.Any(o => o.Contrast < parts[i].Contrast * 0.9)) notes.Add("más contraste");
                if (people)
                {
                    var f = list[i].Faces;
                    if (f is { ClosedEyes: > 0 }) notes.Add(f.ClosedEyes == 1 ? "un ojo cerrado" : "ojos cerrados");
                    else if (f is { OpenEyes: > 0 } && list.Any(o => o.Faces is { ClosedEyes: > 0 })) notes.Add("todos con los ojos abiertos");
                    if (f is { CutFaces: > 0 }) notes.Add("cara cortada por el borde");
                    if (f != null && f.Faces < maxFaces) notes.Add("salen menos personas");
                }
                if (list[i].Item.LiveClip != null && list.Any(o => o.Item.LiveClip == null)) notes.Add("conserva su Live Photo");
                if (parts[i].Exposure < 0.8) notes.Add("zonas quemadas o muy oscuras");
                if (parts[i].Sharp < 0.85) notes.Add("algo movida o borrosa");
                if (EditedVersions.IsEdited(list[i].Item.Media.RelativePath)) notes.Add("versión editada");
            }
            list[i].Notes = string.Join(" · ", notes);
        }
        return best;
    }

    /// <summary>Aparta a <see cref="QuarantineFolder"/> (con la misma ruta relativa) las fotos indicadas y su clip de Live Photo.</summary>
    public MoveResult Quarantine(string root, ManifestStore? store, IEnumerable<AlbumItem> items,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var albums = AlbumService.List(root);
        var plan = new MovePlan { Scanned = MediaScanner.Scan(root, ct, includeAlbums: true) };
        var size = plan.Scanned.ToDictionary(f => f.RelativePath, f => f.Size, StringComparer.OrdinalIgnoreCase);
        foreach (var rel in items.SelectMany(i => new[] { i.Media.RelativePath, i.LiveClip }).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
            plan.Items.Add(new MoveItem(rel!, Path.Combine(QuarantineFolder, rel!), size.GetValueOrDefault(rel!)));
        var result = FileMover.Apply(root, store, plan, _log, progress, ct);
        AlbumService.EnsureFolders(root, albums); // apartar la última foto de un álbum no debe borrar el álbum
        result.ReportPath = FileMover.WriteReport(root, "duplicados", "Fotos repetidas apartadas a " + QuarantineFolder, plan, result);
        _log.Info($"Duplicados: {result.Moved} archivos apartados a {QuarantineFolder}, {result.Failed.Count} fallidos.");
        return result;
    }

    // ------------------------------------------------------------------ Huellas

    private static Dictionary<string, FrameStats> ReadAll(string root, IReadOnlyList<AlbumItem> items,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var cachePath = Path.Combine(ManifestStore.MetaFolder(root), "dhash.json");
        var cache = new ConcurrentDictionary<string, FrameStats>();
        try
        {
            if (File.Exists(cachePath))
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, FrameStats>>(File.ReadAllText(cachePath)) ?? new())
                    cache[kv.Key] = kv.Value;
        }
        catch { /* caché ilegible: se recalcula */ }

        var result = new ConcurrentDictionary<string, FrameStats>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        var changed = false;
        try
        {
        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, item =>
        {
            var key = Key(item.Media);
            if (!cache.TryGetValue(key, out var s) || s.Version < StatsVersion) // caché de una versión anterior: faltan medidas
            {
                s = Compute(Path.Combine(root, item.Media.RelativePath));
                if (s != null) { cache[key] = s; changed = true; }
            }
            if (s != null) result[item.Media.RelativePath] = s;
            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0) progress?.Report((n, items.Count));
        });
        }
        finally // al cancelar se conserva lo ya calculado
        {
        if (changed)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                File.WriteAllText(cachePath, JsonSerializer.Serialize(cache));
            }
            catch { /* sin caché solo es más lento */ }
        }
        return new Dictionary<string, FrameStats>(result, StringComparer.OrdinalIgnoreCase);
    }

    private static FrameStats? Compute(string path)
    {
        try
        {
            int w, h;
            using (var s = File.OpenRead(path))
            {
                var f = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                (w, h) = (f.PixelWidth, f.PixelHeight);
            }
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.DecodePixelWidth = 256;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.EndInit();
            var gray = new FormatConvertedBitmap(img, PixelFormats.Gray8, null, 0);
            int gw = gray.PixelWidth, gh = gray.PixelHeight;
            var px = new byte[gw * gh];
            gray.CopyPixels(px, gw, 0);
            var (sharp, local) = Sharpness(px, gw, gh);
            double sum = 0, sumSq = 0;
            long clipped = 0;
            foreach (var v in px) { sum += v; sumSq += (double)v * v; if (v <= 5 || v >= 250) clipped++; }
            var mean = sum / px.Length;
            return new FrameStats(DHash(px, gw, gh), sharp, w, h, local, clipped / (double)px.Length, mean,
                Math.Sqrt(Math.Max(0, sumSq / px.Length - mean * mean)), StatsVersion);
        }
        catch
        {
            return null; // ilegible (p. ej. sin extensión HEIF): no entra en la comparación
        }
    }

    // Reduce a 9×8 promediando cajas y compara cada píxel con el de su derecha: 64 bits.
    private static ulong DHash(byte[] px, int w, int h)
    {
        var small = new double[8, 9];
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 9; x++)
            {
                int x0 = x * w / 9, x1 = Math.Max(x0 + 1, (x + 1) * w / 9), y0 = y * h / 8, y1 = Math.Max(y0 + 1, (y + 1) * h / 8);
                double sum = 0;
                for (var yy = y0; yy < y1; yy++) for (var xx = x0; xx < x1; xx++) sum += px[yy * w + xx];
                small[y, x] = sum / ((x1 - x0) * (y1 - y0));
            }
        ulong hash = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                hash = (hash << 1) | (small[y, x] < small[y, x + 1] ? 1UL : 0UL);
        return hash;
    }

    // Varianza del laplaciano (más alta cuanto más bordes nítidos hay): de toda la foto y la media del 20 % de cuadrículas (4×4) más nítidas.
    private static (double Global, double Local) Sharpness(byte[] px, int w, int h)
    {
        if (w < 3 || h < 3) return (0, 0);
        const int G = 4;
        var tiles = new (double Sum, double SumSq, int N)[G * G];
        double sum = 0, sumSq = 0;
        var n = 0;
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                double l = 4 * px[y * w + x] - px[y * w + x - 1] - px[y * w + x + 1] - px[(y - 1) * w + x] - px[(y + 1) * w + x];
                sum += l; sumSq += l * l; n++;
                ref var t = ref tiles[(y * G / h) * G + x * G / w];
                t.Sum += l; t.SumSq += l * l; t.N++;
            }
        var mean = sum / n;
        var local = tiles.Where(t => t.N > 0).Select(t => t.SumSq / t.N - Math.Pow(t.Sum / t.N, 2))
            .OrderByDescending(v => v).Take(Math.Max(1, G * G / 5)).Average();
        return (sumSq / n - mean * mean, local);
    }
}
