using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Connection;

/// <summary>
/// Un error contado al usuario: una causa, una acción y «Reintentar». Lo técnico (tipo, mensaje, rutas) va en
/// <see cref="Details"/>, que la ventana enseña plegado. <see cref="GuideRowId"/> enlaza con la guía de conexión, si aplica.
/// </summary>
public sealed record UserError(string Cause, string Action, string Details, string? GuideRowId);

/// <summary>El archivo llegó con un tamaño distinto del que dice el dispositivo.</summary>
public sealed class IncompleteFileException : IOException
{
    public IncompleteFileException(long received, long expected)
        : base($"Tamaño recibido ({received}) distinto del esperado ({expected}).") { }
}

public static class ErrorExplainer
{
    private const int DiskFull = 112, HandleDiskFull = 39;

    public static UserError Explain(Exception ex)
    {
        var details = Details(ex);
        if (IsDiskFull(ex))
            return new("No queda espacio en el disco del PC.", "Libera espacio o elige otra carpeta de destino y pulsa «Reintentar».", details, null);
        if (Find<UnauthorizedAccessException>(ex) != null)
            return new("La app no puede escribir en la carpeta de destino.",
                "Elige otra carpeta (por ejemplo, dentro de Imágenes) y pulsa «Reintentar».", details, null);

        var problem = ConnectionAssistant.Classify(ex);
        if (problem == ConnectionProblem.Unknown && ex is DeviceLostException) problem = ConnectionProblem.NotVisible;
        if (problem != ConnectionProblem.Unknown)
        {
            var row = ConnectionGuide.Row(ConnectionGuide.RowIdFor(problem));
            return new(row.Cause, row.Action, details, row.Id);
        }
        return new("No se pudo terminar la operación.",
            "Pulsa «Reintentar». Si vuelve a pasar, abre «Ayuda de conexión» y pulsa «Diagnóstico».", details, "sigue-sin-funcionar");
    }

    /// <summary>Motivo corto y llano de por qué no se copió UN archivo (para la lista «no copiados» y el informe).</summary>
    public static string FileReason(Exception ex)
    {
        if (IsDiskFull(ex)) return "No queda espacio en el disco del PC";
        if (Find<UnauthorizedAccessException>(ex) != null) return "Sin permiso para escribir en la carpeta de destino";
        if (Find<IncompleteFileException>(ex) != null) return "Llegó incompleto (tamaño distinto)";
        return "El iPhone no dejó leer el archivo";
    }

    private static bool IsDiskFull(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is IOException && (e.HResult & 0xFFFF) is DiskFull or HandleDiskFull) return true;
        return false;
    }

    private static T? Find<T>(Exception ex) where T : Exception
    {
        for (var e = ex; e != null; e = e.InnerException) if (e is T t) return t;
        return null;
    }

    private static string Details(Exception ex)
    {
        var lines = new List<string>();
        for (var e = ex; e != null; e = e.InnerException) lines.Add($"{e.GetType().Name} (0x{e.HResult:X8}): {e.Message}");
        return string.Join(Environment.NewLine, lines);
    }
}
