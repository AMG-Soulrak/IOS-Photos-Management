using System.Text;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Transfer;

/// <summary>
/// Informe de la copia, en texto (decisión del usuario): lo mismo va a <c>ultima-copia.txt</c> y a la pantalla final.
/// Sin número de serie, nombre del dispositivo ni nombres de archivo, para poder enseñarlo sin exponer nada.
/// </summary>
public static class CopyReport
{
    public const string ICloudDeleteWarning =
        "Con Fotos de iCloud activas, borrar una foto en el iPhone también la borra de iCloud y de tus otros dispositivos.";

    public const string NeverDeletes = "La app no borra nada del iPhone: borrar es decisión tuya, desde la app Fotos y tras comprobar la copia.";

    /// <summary>Frase sobre iCloud. Solo afirma algo si el dispositivo lo informa; si no, dice que no se pudo comprobar.</summary>
    public static string CloudLine(TransferSummary s) => s.OnlyInCloud switch
    {
        null => "Solo en iCloud: no se pudo comprobar (el dispositivo no informa de cuántas fotos tiene en total).",
        0 => $"Solo en iCloud: ninguna según el dispositivo (declara {s.DeclaredOnDevice} archivos y se han visto {s.SeenOnDevice}).",
        var n => $"Puede haber {n} fotos solo en iCloud: no están en el teléfono y no se han podido copiar."
    };

    public static string Build(TransferSummary s, IReadOnlyDictionary<FileState, int> counts, DateTime when)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Última copia: {when:yyyy-MM-dd HH:mm} · duración {Format.Duration(s.Duration)}");
        sb.AppendLine($"Copiadas en esta sesión: {s.Copied} ({Format.Bytes(s.BytesCopied)})");
        sb.AppendLine($"Ya estaban: {s.AlreadyDone}" + (s.Adopted > 0 ? $" (de ellas, {s.Adopted} adoptadas sin verificar)" : ""));
        sb.AppendLine($"No copiadas: {s.Failed}");
        foreach (var g in s.FailureReasons.Values.GroupBy(r => r).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  · {g.Key}: {g.Count()}");
        int C(FileState f) => counts.TryGetValue(f, out var n) ? n : 0;
        sb.AppendLine($"En total en la carpeta: {C(FileState.Verified)} verificadas, {C(FileState.Downloaded)} copiadas sin verificar, " +
                      $"{C(FileState.AdoptedUnverified)} adoptadas sin verificar.");
        sb.AppendLine(CloudLine(s));
        if (s.NoCaptureDate > 0)
            sb.AppendLine($"Sin fecha de captura fiable: {s.NoCaptureDate} (se usó la fecha que da el iPhone; pueden quedar en otra carpeta de fecha).");
        if (s.EditedVersions > 0)
            sb.AppendLine($"Versiones editadas (IMG_E…): {s.EditedVersions}. No son repetidas: son la misma foto antes y después de editarla.");
        if (s.JpgCreated + s.JpgFailed > 0)
            sb.AppendLine($"Copias JPG: {s.JpgCreated} creadas" + (s.JpgFailed > 0 ? $", {s.JpgFailed} no se pudieron crear" : "") + ".");
        if (s.Reconnects > 0) sb.AppendLine($"Reconexiones: {s.Reconnects} (la copia siguió donde lo dejó).");
        if (s.SnapshotWarning != null) sb.AppendLine("Aviso: " + s.SnapshotWarning);
        foreach (var n in s.Notices) sb.AppendLine("Aviso: " + n);
        sb.AppendLine("Solo las verificadas a fondo se han comparado con el iPhone.");
        sb.AppendLine(ICloudDeleteWarning);
        sb.AppendLine(NeverDeletes);
        return sb.ToString();
    }

    /// <summary>¿Ya se copió antes a esta carpeta? (entonces la acción por defecto es «copiar solo lo nuevo»).</summary>
    public static bool HasPreviousCopy(string destinationRoot)
        => File.Exists(Path.Combine(destinationRoot, TransferSession.LastCopyReportName));
}
