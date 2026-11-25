namespace IPhonePhotos.Core.Organize;

/// <summary>Estructura de carpetas por fecha: <c>Año\Mes\archivo</c> (p. ej. <c>2024\03\IMG_0001.HEIC</c>).</summary>
public static class DateLayout
{
    public const string NoDateFolder = "Sin fecha";

    /// <summary>Fechas anteriores a esta se consideran "sin fecha" (un dispositivo sin reloj suele informar 1970 o 1980).</summary>
    private const int MinPlausibleYear = 1990;

    public static string Map(string relativePath, DateTime? date)
    {
        var name = Path.GetFileName(relativePath);
        var folder = date is { } d && d.Year >= MinPlausibleYear && d <= DateTime.Now.AddDays(2)
            ? Path.Combine(d.Year.ToString("0000"), d.Month.ToString("00"))
            : NoDateFolder;
        return Path.Combine(folder, name);
    }
}
