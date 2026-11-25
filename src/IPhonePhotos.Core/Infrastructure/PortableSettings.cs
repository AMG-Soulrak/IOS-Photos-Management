using System.Text.Json;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core;

/// <summary>
/// Ajustes de la app en modo portable: un archivo JSON junto al .exe (nada en el registro ni en AppData).
/// Si esa carpeta es de solo lectura (p. ej. un USB protegido), simplemente no se recuerdan los ajustes.
/// </summary>
public sealed class PortableSettings
{
    public string? Destination { get; set; }
    public AdoptMode Adopt { get; set; } = AdoptMode.Fast;
    public bool VerifyDeep { get; set; }

    /// <summary>Guardar las descargas nuevas en carpetas Año\Mes.</summary>
    public bool OrganizeByDate { get; set; }

    /// <summary>Las fotos nuevas van a la carpeta «Nuevos» (para repartirlas luego en álbumes).</summary>
    public bool UseNewFolder { get; set; } = true;

    /// <summary>"" = automático; o wpd / afc / backup.</summary>
    public string Technique { get; set; } = "";

    public bool AllowSnapshotFallback { get; set; } = true;

    /// <summary>"" = igual que Windows; "light" o "dark".</summary>
    public string Theme { get; set; } = "";

    public static string FilePath
    {
        get
        {
            // ProcessPath es la ubicación real del .exe aunque la app esté empaquetada en un único archivo.
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "iphotos");
            return Path.Combine(Path.GetDirectoryName(exe)!, "iphotos.settings.json");
        }
    }

    public static PortableSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PortableSettings>(File.ReadAllText(FilePath)) ?? new PortableSettings();
        }
        catch
        {
            // ajustes ilegibles: se empieza de cero
        }
        return new PortableSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // carpeta de solo lectura: no pasa nada
        }
    }
}
