using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IPhonePhotos.Core.Plist;

/// <summary>Referencia a otro objeto dentro de un plist binario (la usa NSKeyedArchiver).</summary>
public readonly record struct PlistUid(ulong Value);

/// <summary>
/// Lector mínimo de listas de propiedades de Apple (plist), en formato binario (bplist00) y XML.
/// Devuelve: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, string, long, double, bool, DateTime (UTC),
/// byte[], <see cref="PlistUid"/> o null. Solo lectura, sin dependencias.
/// </summary>
public static class PlistReader
{
    private static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static object? ParseFile(string path) => Parse(File.ReadAllBytes(path));

    public static object? Parse(byte[] data)
    {
        if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == "bplist00") return ParseBinary(data);
        return ParseXml(data);
    }

    // ------------------------------------------------------------------ Binario

    private static object? ParseBinary(byte[] d)
    {
        if (d.Length < 40) throw new FormatException("Plist binario demasiado corto.");
        var t = d.Length - 32;
        int offsetSize = d[t + 6];
        int refSize = d[t + 7];
        var numObjects = BigEndian(d, t + 8, 8);
        var top = BigEndian(d, t + 16, 8);
        var offsetTable = BigEndian(d, t + 24, 8);

        if (offsetSize < 1 || offsetSize > 8 || refSize < 1 || refSize > 8 || numObjects <= 0 || top >= numObjects ||
            offsetTable + numObjects * offsetSize > d.Length)
            throw new FormatException("Plist binario con cabecera no válida.");

        long OffsetOf(long index)
        {
            if (index < 0 || index >= numObjects) throw new FormatException("Referencia fuera de rango en el plist.");
            return BigEndian(d, (int)(offsetTable + index * offsetSize), offsetSize);
        }

        object? Read(long index, int depth)
        {
            if (depth > 64) throw new FormatException("Plist demasiado anidado.");
            var pos = (int)OffsetOf(index);
            var marker = d[pos];
            var type = marker >> 4;
            var info = marker & 0x0F;

            // Longitud de las colecciones/cadenas: en el propio marcador o en un entero a continuación.
            (int Count, int Header) Length()
            {
                if (info != 0x0F) return (info, 1);
                var m2 = d[pos + 1];
                var n = 1 << (m2 & 0x0F);
                return ((int)BigEndian(d, pos + 2, n), 2 + n);
            }

            switch (type)
            {
                case 0x0:
                    return info switch { 0x0 => null, 0x8 => false, 0x9 => true, _ => null };
                case 0x1:
                {
                    var n = 1 << info;
                    return BigEndian(d, pos + 1, n);
                }
                case 0x2:
                {
                    var n = 1 << info;
                    if (n == 4) return BitConverter.Int32BitsToSingle((int)BigEndian(d, pos + 1, 4));
                    return BitConverter.Int64BitsToDouble(BigEndian(d, pos + 1, 8));
                }
                case 0x3:
                {
                    var seconds = BitConverter.Int64BitsToDouble(BigEndian(d, pos + 1, 8));
                    return AppleEpoch.AddSeconds(seconds);
                }
                case 0x4:
                {
                    var (count, header) = Length();
                    var bytes = new byte[count];
                    Buffer.BlockCopy(d, pos + header, bytes, 0, count);
                    return bytes;
                }
                case 0x5:
                {
                    var (count, header) = Length();
                    return Encoding.ASCII.GetString(d, pos + header, count);
                }
                case 0x6:
                {
                    var (count, header) = Length();
                    return Encoding.BigEndianUnicode.GetString(d, pos + header, count * 2);
                }
                case 0x8:
                    return new PlistUid((ulong)BigEndian(d, pos + 1, info + 1));
                case 0xA:
                case 0xC:
                {
                    var (count, header) = Length();
                    var list = new List<object?>(count);
                    for (var i = 0; i < count; i++)
                        list.Add(Read(BigEndian(d, pos + header + i * refSize, refSize), depth + 1));
                    return list;
                }
                case 0xD:
                {
                    var (count, header) = Length();
                    var dict = new Dictionary<string, object?>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var key = Read(BigEndian(d, pos + header + i * refSize, refSize), depth + 1) as string ?? "";
                        var val = Read(BigEndian(d, pos + header + (count + i) * refSize, refSize), depth + 1);
                        dict[key] = val;
                    }
                    return dict;
                }
                default:
                    throw new FormatException($"Tipo de objeto de plist no soportado: 0x{marker:X2}");
            }
        }

        return Read(top, 0);
    }

    private static long BigEndian(byte[] d, int pos, int n)
    {
        // Para enteros de 16 bytes solo importan los 8 últimos.
        if (n > 8) { pos += n - 8; n = 8; }
        long v = 0;
        for (var i = 0; i < n; i++) v = (v << 8) | d[pos + i];
        return v;
    }

    // ------------------------------------------------------------------ XML

    private static object? ParseXml(byte[] data)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var ms = new MemoryStream(data);
        using var reader = XmlReader.Create(ms, settings);
        var doc = XDocument.Load(reader);
        var first = doc.Root?.Elements().FirstOrDefault();
        return first == null ? null : FromXml(first);
    }

    private static object? FromXml(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "dict":
            {
                var dict = new Dictionary<string, object?>();
                var items = e.Elements().ToList();
                for (var i = 0; i + 1 < items.Count; i += 2)
                    dict[items[i].Value] = FromXml(items[i + 1]);
                return dict;
            }
            case "array": return e.Elements().Select(FromXml).ToList();
            case "string": return e.Value;
            case "integer": return long.TryParse(e.Value, out var l) ? l : 0L;
            case "real": return double.TryParse(e.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0d;
            case "true": return true;
            case "false": return false;
            case "date": return DateTime.TryParse(e.Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
            case "data": return Convert.FromBase64String(string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c))));
            default: return null;
        }
    }

    // ------------------------------------------------------------------ Utilidades

    /// <summary>Para archivos serializados con NSKeyedArchiver (como el campo "file" de Manifest.db): devuelve el objeto raíz.</summary>
    public static Dictionary<string, object?>? KeyedArchiveRoot(object? parsed)
    {
        if (parsed is not Dictionary<string, object?> d) return null;
        if (d.GetValueOrDefault("$objects") is not List<object?> objects) return null;
        if (d.GetValueOrDefault("$top") is not Dictionary<string, object?> top) return null;
        if (top.GetValueOrDefault("root") is not PlistUid uid) return null;
        var index = (int)uid.Value;
        return index >= 0 && index < objects.Count ? objects[index] as Dictionary<string, object?> : null;
    }

    public static string? GetString(Dictionary<string, object?>? d, string key) => d?.GetValueOrDefault(key) as string;

    public static long? GetLong(Dictionary<string, object?>? d, string key) => d?.GetValueOrDefault(key) switch
    {
        long l => l,
        double x => (long)x,
        _ => null
    };

    public static bool GetBool(Dictionary<string, object?>? d, string key) => d?.GetValueOrDefault(key) is true;

    public static DateTime? GetDate(Dictionary<string, object?>? d, string key) => d?.GetValueOrDefault(key) as DateTime?;
}
