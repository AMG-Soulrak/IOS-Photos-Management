namespace IPhonePhotos.Core;

/// <summary>
/// Rutas relativas que llegan de fuera (iPhone, copia de seguridad, manifest.db): nunca deben salir de la carpeta de destino.
/// Una copia de seguridad o un manifiesto manipulados podrían traer "DCIM\..\..\Inicio\x.lnk" o "C:\...".
/// </summary>
public static class SafePath
{
    /// <summary>True si la ruta es relativa, sin unidad, sin «..» y, combinada con <paramref name="root"/>, queda dentro.</summary>
    public static bool IsInside(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':')) return false;
        if (relativePath.Split('\\', '/').Any(s => s == "..")) return false;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Combina y garantiza que el resultado queda dentro de <paramref name="root"/>; si no, lanza.</summary>
    public static string Join(string root, string relativePath) =>
        IsInside(root, relativePath)
            ? Path.GetFullPath(Path.Combine(root, relativePath))
            : throw new InvalidDataException($"Ruta rechazada: sale de la carpeta de destino ({relativePath}).");
}
