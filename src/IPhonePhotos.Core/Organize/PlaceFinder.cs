using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using IPhonePhotos.Core.Manifest;

namespace IPhonePhotos.Core.Organize;

/// <summary>
/// Pone nombre a unas coordenadas GPS buscando la ciudad más cercana en la lista pública de GeoNames
/// (ciudades de más de 15.000 habitantes, licencia CC BY 4.0). Es todo local: solo se descarga el archivo de la lista, una vez.
/// shortcut: devuelve la ciudad más cercana dentro de <see cref="MaxKm"/>, no el barrio ni el municipio exacto; para más
/// detalle habría que usar la lista de 1.000 habitantes (cities1000.zip, mucho más grande).
/// </summary>
public sealed class PlaceFinder
{
    public const double MaxKm = 50;
    public const string DownloadUrl = "https://download.geonames.org/export/dump/cities15000.zip";
    private const string EntryName = "cities15000.txt";

    private readonly Dictionary<(int, int), List<(double Lat, double Lon, string Name)>> _grid = new();

    public static string DataPath(string root) => Path.Combine(ManifestStore.MetaFolder(root), "places", EntryName);

    /// <summary>Null si todavía no se han descargado los nombres de lugares.</summary>
    public static PlaceFinder? Load(string root)
    {
        var path = DataPath(root);
        if (!File.Exists(path)) return null;
        var finder = new PlaceFinder();
        foreach (var line in File.ReadLines(path))
        {
            var c = line.Split('\t'); // 1 = nombre, 4 = latitud, 5 = longitud
            if (c.Length < 6 || !double.TryParse(c[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                             || !double.TryParse(c[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            var cell = ((int)Math.Floor(lat), (int)Math.Floor(lon));
            if (!finder._grid.TryGetValue(cell, out var list)) finder._grid[cell] = list = new();
            list.Add((lat, lon, c[1]));
        }
        return finder;
    }

    public static async Task DownloadAsync(string root, CancellationToken ct)
    {
        var target = DataPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        await using var zip = new MemoryStream(await http.GetByteArrayAsync(DownloadUrl, ct));
        using var archive = new ZipArchive(zip);
        var entry = archive.GetEntry(EntryName) ?? throw new InvalidDataException($"El archivo descargado no contiene {EntryName}.");
        var tmp = target + ".tmp";
        await using (var input = entry.Open())
        await using (var output = File.Create(tmp))
            await input.CopyToAsync(output, ct);
        File.Move(tmp, target, overwrite: true);
    }

    /// <summary>Nombre de la ciudad más cercana, o null si no hay ninguna a menos de <see cref="MaxKm"/>.</summary>
    public string? Find(double lat, double lon)
    {
        string? best = null;
        var bestKm = MaxKm;
        var (cy, cx) = ((int)Math.Floor(lat), (int)Math.Floor(lon));
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (_grid.TryGetValue((cy + dy, cx + dx), out var list))
                    foreach (var (la, lo, name) in list)
                    {
                        var km = Haversine(lat, lon, la, lo);
                        if (km < bestKm) { bestKm = km; best = name; }
                    }
        return best;
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371, Rad = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * Rad / 2), 2)
              + Math.Cos(lat1 * Rad) * Math.Cos(lat2 * Rad) * Math.Pow(Math.Sin((lon2 - lon1) * Rad / 2), 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}
