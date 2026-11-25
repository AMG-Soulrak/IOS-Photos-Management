using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Windows.Media.Imaging;
using IPhonePhotos.Core.Manifest;

namespace IPhonePhotos.Core.Organize;

/// <summary>Lo que dicen los metadatos EXIF de una foto: cuándo se hizo y dónde (GPS). Todo puede faltar.</summary>
public sealed record PhotoInfo(DateTime? Taken, double? Lat, double? Lon);

/// <summary>
/// Lee fecha de toma y GPS de las fotos (.HEIC/.JPG) con el decodificador de Windows. Leer miles de fotos tarda,
/// así que el resultado se guarda en <c>.iphotos\photoinfo.json</c> y la segunda vez es instantáneo.
/// </summary>
public static class PhotoInfoReader
{
    private static readonly HashSet<string> Stills = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".jpg", ".jpeg", ".png" };

    public static bool IsStill(string path) => Stills.Contains(Path.GetExtension(path));

    // La clave no incluye la carpeta: si la foto cambia de carpeta, la caché sigue valiendo.
    private static string Key(LocalMedia f) => $"{Path.GetFileName(f.RelativePath)}|{f.Size}|{f.Date.Ticks}";

    public static Dictionary<string, PhotoInfo> ReadAll(string root, IReadOnlyList<LocalMedia> files,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var cachePath = Path.Combine(ManifestStore.MetaFolder(root), "photoinfo.json");
        var cache = new ConcurrentDictionary<string, PhotoInfo>();
        try
        {
            if (File.Exists(cachePath))
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, PhotoInfo>>(File.ReadAllText(cachePath)) ?? new())
                    cache[kv.Key] = kv.Value;
        }
        catch { /* caché ilegible: se vuelve a leer todo */ }

        var result = new ConcurrentDictionary<string, PhotoInfo>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        var changed = false;
        Parallel.ForEach(files.Where(f => IsStill(f.RelativePath)),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, f =>
            {
                var key = Key(f);
                if (!cache.TryGetValue(key, out var info))
                {
                    info = ReadOne(Path.Combine(root, f.RelativePath));
                    cache[key] = info;
                    changed = true;
                }
                result[f.RelativePath] = info;
                var n = Interlocked.Increment(ref done);
                if (n % 25 == 0) progress?.Report((n, files.Count));
            });

        if (changed)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                File.WriteAllText(cachePath, JsonSerializer.Serialize(cache));
            }
            catch { /* sin caché solo es más lento */ }
        }
        return new Dictionary<string, PhotoInfo>(result, StringComparer.OrdinalIgnoreCase);
    }

    private static PhotoInfo ReadOne(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            var frame = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            if (frame.Metadata is not BitmapMetadata m) return new(null, null, null);

            // HEIC lo cuelga de "/ifd/…" y JPG de "/app1/ifd/…".
            object? Q(string q)
            {
                foreach (var prefix in new[] { "", "/app1" })
                    try { if (m.GetQuery(prefix + q) is { } v) return v; } catch { }
                return null;
            }

            DateTime? taken = DateTime.TryParseExact(Q("/ifd/exif/{ushort=36867}") as string, "yyyy:MM:dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            return new(taken, Coord(Q("/ifd/gps/{ushort=2}"), Q("/ifd/gps/{ushort=1}") as string),
                              Coord(Q("/ifd/gps/{ushort=4}"), Q("/ifd/gps/{ushort=3}") as string));
        }
        catch
        {
            return new(null, null, null); // foto sin metadatos legibles: se usará la fecha del archivo
        }
    }

    // El GPS llega como 3 fracciones (grados, minutos, segundos), cada una empaquetada en un ulong: numerador abajo, denominador arriba.
    private static double? Coord(object? dms, string? reference)
    {
        if (dms is not ulong[] { Length: 3 } v) return null;
        double Part(ulong x) => (x >> 32) == 0 ? double.NaN : (double)(uint)x / (uint)(x >> 32);
        var value = Part(v[0]) + Part(v[1]) / 60 + Part(v[2]) / 3600;
        if (double.IsNaN(value)) return null;
        return reference is "S" or "W" ? -value : value;
    }
}
