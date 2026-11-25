using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Transfer;

/// <summary>
/// Orquesta una copia completa, resistente a cortes y a técnicas que fallan:
///  1) Prueba las técnicas disponibles para el dispositivo y las ordena (WPD, AFC…).
///  2) Copia con la mejor. Si el dispositivo se desconecta, espera a que vuelva y continúa por lo que faltaba.
///  3) Si una técnica no abre, no lista nada o deja archivos sin copiar, pasa a la siguiente: el manifiesto es común,
///     así que la siguiente solo copia lo pendiente.
///  4) Como último recurso (y avisando) puede leer la última copia de seguridad de ese mismo iPhone.
/// </summary>
public sealed class TransferSession
{
    private readonly BackendSelector _selector;
    private readonly ActivityLog _log;
    private readonly TransferEngine _engine;

    public TransferSession(BackendSelector selector, ActivityLog log)
    {
        _selector = selector;
        _log = log;
        _engine = new TransferEngine(log);
    }

    private enum Outcome { Completed, CompletedEmpty, Unavailable }

    public async Task<TransferSummary> RunAsync(
        DeviceDescriptor device,
        TransferOptions options,
        IProgress<TransferProgress>? progress,
        IProgress<string>? status,
        CancellationToken ct)
    {
        Directory.CreateDirectory(options.DestinationRoot);
        _log.AttachFile(Path.Combine(ManifestStore.MetaFolder(options.DestinationRoot), $"log-{DateTime.Now:yyyyMMdd}.txt"));

        var summary = new TransferSummary();
        var reasons = new List<string>();
        var anyOpened = false;
        var liveCompletedWithFiles = false;

        // Si el dispositivo no está visible todavía, se espera a que aparezca (cancelable).
        status?.Report("Buscando el dispositivo…");
        var ordered = await WaitForTechniquesAsync(device, options, summary, progress, status, ct).ConfigureAwait(false);

        // Con varias técnicas en vivo, se prueban y se ordenan por puntuación.
        if (ordered.Count(t => !t.IsSnapshot) > 1)
        {
            status?.Report("Probando las técnicas de conexión…");
            (ordered, _) = await _selector.RankAsync(device, options.OnlyTechnique, ct).ConfigureAwait(false);
        }

        _log.Info("Técnicas disponibles: " + string.Join(" → ", ordered.Select(t => t.Name + (t.IsSnapshot ? " (copia de seguridad)" : ""))));

        var onlySnapshots = ordered.All(t => t.IsSnapshot);
        for (var i = 0; i < ordered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tech = ordered[i];

            if (tech.IsSnapshot && !onlySnapshots)
            {
                if (!options.AllowSnapshotFallback) { _log.Info("Copia de seguridad descartada por configuración."); continue; }
                if (liveCompletedWithFiles && summary.Failed == 0) continue; // ya está todo: no hace falta
                _log.Warn($"Se recurre a la copia de seguridad ({tech.Device.Detail}). Puede estar desactualizada.");
            }

            var (outcome, files, usedTech) = await RunTechniqueAsync(tech, device, options, summary, reasons, progress, status, ct)
                .ConfigureAwait(false);
            if (outcome != Outcome.Unavailable) anyOpened = true;

            if (outcome == Outcome.Completed)
            {
                if (usedTech.IsSnapshot)
                    summary.SnapshotWarning = $"Se han leído datos de una copia de seguridad ({usedTech.Device.Detail}). " +
                                              "Las fotos hechas después de esa copia no se han podido copiar.";
                else liveCompletedWithFiles = true;

                if (summary.Failed == 0) break; // todo copiado
                if (i < ordered.Count - 1)
                    _log.Warn($"{summary.Failed} archivo(s) siguen sin copiar: se prueba con la siguiente técnica.");
            }
            else if (outcome == Outcome.CompletedEmpty)
            {
                _log.Warn($"{usedTech.Name} no muestra ninguna foto. " +
                          (i < ordered.Count - 1 ? "Se prueba con la siguiente técnica." : ""));
            }
        }

        if (!anyOpened)
            throw new DeviceLostException("No se pudo abrir el dispositivo con ninguna técnica:\n· " + string.Join("\n· ", reasons));

        if (summary.Copied == 0 && summary.AlreadyDone == 0 && summary.Failed == 0)
            _log.Warn("No se ve ninguna foto con ninguna técnica. Comprueba que el iPhone esté desbloqueado y que hayas pulsado \"Confiar\".");

        WriteLastCopyReport(options.DestinationRoot, summary);
        progress?.Report(new TransferProgress(TransferPhase.Done, 0, 0, 0, 0, null, 0, 0, 0));
        return summary;
    }

    public const string LastCopyReportName = "ultima-copia.txt";

    /// <summary>
    /// Informe local y simple de la última copia (decidido por el usuario): fecha y recuentos. Sin número de serie,
    /// nombre del dispositivo ni nombres de archivo, para poder enseñarlo sin exponer nada.
    /// </summary>
    private void WriteLastCopyReport(string destinationRoot, TransferSummary summary)
    {
        try
        {
            Dictionary<FileState, int> counts;
            using (var store = ManifestStore.Open(destinationRoot)) counts = store.CountByState();
            var text =
                $"Última copia: {DateTime.Now:yyyy-MM-dd HH:mm}\r\n" +
                $"Copiadas en esta sesión: {summary.Copied}\r\n" +
                $"Ya estaban: {summary.AlreadyDone}\r\n" +
                $"Fallidas: {summary.Failed}\r\n" +
                $"En total en la carpeta: {counts[FileState.Verified]} verificadas, {counts[FileState.Downloaded]} copiadas sin verificar, " +
                $"{counts[FileState.AdoptedUnverified]} adoptadas sin verificar.\r\n" +
                "Solo las verificadas a fondo se han comparado con el iPhone. La app no borra nada del iPhone.\r\n";
            File.WriteAllText(Path.Combine(destinationRoot, LastCopyReportName), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"No se pudo escribir {LastCopyReportName}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Una técnica

    private async Task<(Outcome Outcome, int Files, Technique Used)> RunTechniqueAsync(
        Technique tech, DeviceDescriptor device, TransferOptions options, TransferSummary summary,
        List<string> reasons, IProgress<TransferProgress>? progress, IProgress<string>? status, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                status?.Report($"Conectando con el dispositivo ({tech.Provider.Key})…");
                using var backend = await OpenAsync(tech, options.OpenTimeout, ct).ConfigureAwait(false);
                _log.Info($"Técnica en uso: {tech.Name}");
                using var store = ManifestStore.Open(options.DestinationRoot);
                try
                {
                    var files = await ScanAndDownloadAsync(backend, store, options, summary, progress, status, ct).ConfigureAwait(false);
                    return (files > 0 ? Outcome.Completed : Outcome.CompletedEmpty, files, tech);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not DeviceLostException && !backend.IsAlive())
                {
                    // Cualquier error con el dispositivo ya ausente (p. ej. durante el escaneo) es un corte de cable.
                    throw new DeviceLostException("El dispositivo se ha desconectado.", ex);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (DeviceLostException ex) when (!tech.IsSnapshot)
            {
                if (summary.Reconnects >= 50) throw;
                _log.Warn($"Conexión perdida: {ex.Message}");
                status?.Report("Dispositivo desconectado. Esperando a que vuelva para reanudar…");
                progress?.Report(new TransferProgress(TransferPhase.Waiting, 0, 0, 0, 0, null, 0, 0, 0));

                var back = await WaitForAnyTechniqueAsync(tech, device, options, ct).ConfigureAwait(false);
                if (back == null)
                    throw new DeviceLostException(
                        $"El dispositivo no volvió en {options.ReconnectTimeout.TotalMinutes:F0} minutos. " +
                        "Lo copiado hasta ahora está guardado: vuelve a pulsar Descargar para continuar.", ex);

                tech = back;
                summary.Reconnects++;
                _log.Info($"Dispositivo de vuelta. Reanudando (reconexión {summary.Reconnects}).");
            }
            catch (Exception ex)
            {
                // No abre (iPhone bloqueado, sin confianza, controlador…), no responde o falla de otra forma:
                // se anota el motivo y se pasa a la siguiente técnica.
                var reason = $"{tech.Name}: {ex.Message}";
                reasons.Add(reason);
                _log.Warn(reason);
                return (Outcome.Unavailable, 0, tech);
            }
        }
    }

    private async Task<int> ScanAndDownloadAsync(IDeviceBackend backend, ManifestStore store, TransferOptions options,
        TransferSummary summary, IProgress<TransferProgress>? progress, IProgress<string>? status, CancellationToken ct)
    {
        status?.Report("Comparando el dispositivo con lo ya descargado…");
        progress?.Report(new TransferProgress(TransferPhase.Scanning, 0, 0, 0, 0, null, 0, 0, 0));
        var scan = await Task.Run(() => _engine.Scan(backend, store, options, status, ct), ct).ConfigureAwait(false);

        _log.Info($"Dispositivo ({backend.Device.Technique}): {scan.All.Count} archivos ({Format.Bytes(scan.TotalBytes)}). " +
                  $"Ya copiados: {scan.AlreadyDone}. Pendientes: {scan.Pending.Count} ({Format.Bytes(scan.PendingBytes)}).");
        if (scan.MissingOnDevice > 0)
            _log.Info($"{scan.MissingOnDevice} archivos constan como copiados pero ya no están en el dispositivo.");

        status?.Report("Descargando…");
        await _engine.RunAsync(backend, store, scan, options, summary, progress, ct).ConfigureAwait(false);
        return scan.All.Count;
    }

    /// <summary>Abre la técnica con un límite de tiempo: una conexión que se cuelga no debe bloquear a las demás.</summary>
    private static async Task<IDeviceBackend> OpenAsync(Technique tech, TimeSpan timeout, CancellationToken ct)
    {
        var task = Task.Run(() => tech.Provider.Open(tech.Device), ct);
        var finished = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (finished != task)
        {
            ct.ThrowIfCancellationRequested();
            // Si acaba abriendo más tarde, se cierra para no dejar la conexión colgada.
            _ = task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            throw new TimeoutException($"No respondió en {timeout.TotalSeconds:F0} segundos.");
        }
        return await task.ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ Esperas

    /// <summary>Si el dispositivo aún no está visible, espera a que aparezca (hasta el tiempo configurado).</summary>
    private async Task<IReadOnlyList<Technique>> WaitForTechniquesAsync(DeviceDescriptor device, TransferOptions options,
        TransferSummary summary, IProgress<TransferProgress>? progress, IProgress<string>? status, CancellationToken ct)
    {
        var now = _selector.Techniques(device, options.OnlyTechnique);
        if (now.Count > 0) return now;

        _log.Warn("El dispositivo no está visible todavía.");
        status?.Report("Esperando a que el dispositivo aparezca (cable, desbloqueo, «Confiar»)…");
        progress?.Report(new TransferProgress(TransferPhase.Waiting, 0, 0, 0, 0, null, 0, 0, 0));

        var deadline = DateTime.UtcNow + options.ReconnectTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(options.ReconnectPollInterval, ct).ConfigureAwait(false);
            now = _selector.Techniques(device, options.OnlyTechnique);
            if (now.Count > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                return _selector.Techniques(device, options.OnlyTechnique);
            }
        }
        throw new DeviceLostException(
            $"No se ve '{device.DisplayName}'. Comprueba el cable, desbloquéalo y pulsa «Confiar», y vuelve a intentarlo.");
    }

    /// <summary>Tras un corte, espera a que vuelva la misma técnica o, si no, cualquier otra técnica en vivo.</summary>
    private async Task<Technique?> WaitForAnyTechniqueAsync(Technique current, DeviceDescriptor device,
        TransferOptions options, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + options.ReconnectTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var all = _selector.Techniques(device, options.OnlyTechnique, includeSnapshots: false);
            var found = all.FirstOrDefault(t => t.Provider == current.Provider) ?? all.FirstOrDefault();
            if (found != null)
            {
                // Pequeña pausa para que Windows termine de montar el dispositivo.
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                return found;
            }
            await Task.Delay(options.ReconnectPollInterval, ct).ConfigureAwait(false);
        }
        return null;
    }
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:F0} {units[i]}" : $"{v:F1} {units[i]}";
    }

    public static string Duration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" :
           t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds} s" : $"{t.Seconds} s";
}
