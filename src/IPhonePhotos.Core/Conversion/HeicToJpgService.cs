using System.Windows.Media.Imaging;

namespace IPhonePhotos.Core.Conversion;

public sealed record ConversionResult(int Converted, int Skipped, IReadOnlyList<string> Failed);

/// <summary>
/// Crea una copia en JPG de cada foto HEIC/HEIF de una carpeta, en OTRA carpeta, con la misma estructura.
/// No toca los originales y se puede cortar y repetir: lo que ya existe en el destino se salta.
/// Usa el decodificador de Windows (WIC), el mismo que da las miniaturas de .HEIC en el Explorador: necesita
/// «Extensiones de imagen HEIF» instalada. shortcut: sin ella falla con un error claro; para no depender de ella
/// habría que añadir una librería propia (p. ej. Magick.NET) a la app portable.
/// </summary>
public sealed class HeicToJpgService
{
    private static readonly string[] Extensions = { ".heic", ".heif" };
    private readonly ActivityLog _log;

    public HeicToJpgService(ActivityLog log) => _log = log;

    public ConversionResult Convert(string source, string target, int quality, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        var targetPrefix = target.TrimEnd('\\') + "\\";
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !p.Contains("\\.iphotos\\", StringComparison.OrdinalIgnoreCase)
                        && !p.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int converted = 0, skipped = 0, done = 0;
        var failed = new List<string>();
        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.ChangeExtension(Path.Combine(target, Path.GetRelativePath(source, src)), ".jpg");
            try
            {
                if (File.Exists(dest)) skipped++;
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    var tmp = dest + ".tmp"; // si se corta a mitad no queda un JPG a medias que luego se dé por bueno
                    ConvertOne(src, tmp, quality);
                    File.Move(tmp, dest);
                    // La fecha del archivo original, para que sigan ordenándose por fecha en el Explorador.
                    File.SetCreationTime(dest, File.GetCreationTime(src));
                    File.SetLastWriteTime(dest, File.GetLastWriteTime(src));
                    converted++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetRelativePath(source, src)}: {ex.Message}");
                _log.Warn($"No se pudo convertir {src}: {ex.Message}");
                try { File.Delete(dest + ".tmp"); } catch { }
            }
            progress?.Report((++done, files.Count));
        }
        return new ConversionResult(converted, skipped, failed);
    }

    public static void ConvertOne(string src, string dest, int quality)
    {
        using var input = File.OpenRead(src);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        try { Save(frame, dest, quality, withMetadata: true); }
        catch (Exception) { Save(frame, dest, quality, withMetadata: false); } // el JPG no admite algún dato del HEIC: se guarda sin metadatos
    }

    private static void Save(BitmapFrame frame, string dest, int quality, bool withMetadata)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(frame, null, withMetadata ? JpegMetadata(frame) : null, frame.ColorContexts));
        using var output = File.Create(dest);
        encoder.Save(output);
    }

    // shortcut: solo se copian fecha de toma, cámara y ubicación GPS; el resto de etiquetas del HEIC se descarta
    // (la orientación no se copia a propósito: el decodificador ya gira los píxeles y se rotaría dos veces).
    private static BitmapMetadata? JpegMetadata(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata src) return null;
        var dst = new BitmapMetadata("jpg");
        foreach (var tag in new[] { "/ifd/{ushort=306}", "/ifd/{ushort=271}", "/ifd/{ushort=272}", "/ifd/exif/{ushort=36867}", "/ifd/exif/{ushort=36868}" })
            CopyTag(src, dst, tag);
        if (Query(src, "/ifd/gps/{ushort=2}") != null)
        {
            dst.SetQuery("/app1/ifd/gps", new BitmapMetadata("gps"));
            for (var n = 1; n <= 6; n++) CopyTag(src, dst, $"/ifd/gps/{{ushort={n}}}");
        }
        return dst;
    }

    private static object? Query(BitmapMetadata m, string q)
    {
        try { return m.GetQuery(q); } catch { return null; } // una etiqueta que falta puede lanzar en vez de devolver null
    }

    private static void CopyTag(BitmapMetadata src, BitmapMetadata dst, string tag)
    {
        if (Query(src, tag) is { } v) dst.SetQuery("/app1" + tag, v);
    }
}
