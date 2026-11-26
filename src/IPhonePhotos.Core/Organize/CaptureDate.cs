using System.Buffers.Binary;

namespace IPhonePhotos.Core.Organize;

/// <summary>
/// Fecha de captura de una foto (EXIF) o de un vídeo (cabecera QuickTime «mvhd»). Null si no hay o no es creíble.
/// shortcut: en vídeo solo se lee mvhd (UTC); la etiqueta de Apple com.apple.quicktime.creationdate (con zona) no se lee.
/// </summary>
public static class CaptureDate
{
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4", ".m4v" };

    /// <param name="path">Archivo a leer (puede ser un .part).</param>
    /// <param name="originalName">Nombre real, para saber si es foto o vídeo.</param>
    public static DateTime? Read(string path, string originalName)
    {
        DateTime? d = null;
        try
        {
            if (Videos.Contains(Path.GetExtension(originalName))) d = ReadQuickTime(path);
            else if (PhotoInfoReader.IsStill(originalName)) d = PhotoInfoReader.ReadFile(path).Taken;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return DateLayout.IsPlausible(d) ? d : null;
    }

    /// <summary>¿Es una foto o un vídeo de los que tienen fecha de captura? (.AAE y otros, no.)</summary>
    public static bool Applies(string name) => Videos.Contains(Path.GetExtension(name)) || PhotoInfoReader.IsStill(name);

    private static readonly DateTime QuickTimeEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DateTime? ReadQuickTime(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var moov = FindAtom(fs, 0, fs.Length, "moov");
        if (moov is not { } m) return null;
        var mvhd = FindAtom(fs, m.Start, m.End, "mvhd");
        if (mvhd is not { } h) return null;
        fs.Position = h.Start;
        var head = new byte[12];
        if (fs.Read(head, 0, 12) < 12) return null;
        var seconds = head[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(4, 8)) : BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4, 4));
        if (seconds == 0 || seconds > 10_000_000_000) return null;
        return DateTime.SpecifyKind(QuickTimeEpoch.AddSeconds(seconds).ToLocalTime(), DateTimeKind.Unspecified);
    }

    /// <summary>Busca un átomo hijo entre <paramref name="start"/> y <paramref name="end"/>; devuelve dónde empieza y acaba su contenido.</summary>
    private static (long Start, long End)? FindAtom(Stream s, long start, long end, string type)
    {
        var header = new byte[16];
        var pos = start;
        while (pos + 8 <= end)
        {
            s.Position = pos;
            if (s.Read(header, 0, 8) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var name = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            var headerLen = 8;
            if (size == 1)
            {
                if (s.Read(header, 8, 8) < 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                headerLen = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < headerLen || pos + size > end) return null;
            if (name == type) return (pos + headerLen, pos + size);
            pos += size;
        }
        return null;
    }
}
