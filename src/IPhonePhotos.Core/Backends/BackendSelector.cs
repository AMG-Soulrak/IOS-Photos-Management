using System.Diagnostics;
using System.Text;
using IPhonePhotos.Core.Connection;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Backends;

/// <summary>Una técnica concreta aplicable a un dispositivo concreto.</summary>
public sealed record Technique(IDeviceBackendProvider Provider, DeviceDescriptor Device)
{
    public string Name => Provider.Technique;
    public bool IsSnapshot => Provider.IsSnapshot;
}

/// <summary>Resultado de probar una técnica contra un dispositivo concreto.</summary>
public sealed record ProbeResult(
    DeviceDescriptor Device,
    string Technique,
    bool Opened,
    string? Error,
    int FilesSeen,
    string? SampleFile,
    long SampleBytes,
    bool CanReadSample,
    double? KiloBytesPerSecond,
    bool ReportsDates,
    bool ReportsSizes,
    TimeSpan ListTime,
    double Score,
    ConnectionProblem Problem = ConnectionProblem.None);

/// <summary>
/// Elige la técnica más apropiada para el dispositivo conectado.
///  · Reconoce que el mismo iPhone visto por WPD, AFC o una copia de seguridad es un único aparato (por su UDID).
///  · Prueba cada técnica en vivo (lista archivos, lee uno pequeño, mide velocidad) y las ordena por puntuación.
///  · Las copias de seguridad (fotos fijas) quedan siempre al final: solo se usan si nada en vivo funciona.
/// </summary>
public sealed class BackendSelector
{
    private readonly ActivityLog _log;

    public BackendSelector(IEnumerable<IDeviceBackendProvider> providers, ActivityLog log)
    {
        Providers = providers.ToList();
        _log = log;
    }

    public IReadOnlyList<IDeviceBackendProvider> Providers { get; }

    // ------------------------------------------------------------------ Descubrimiento y agrupación

    private sealed record Member(IDeviceBackendProvider Provider, DeviceDescriptor Device);

    private List<List<Member>> BuildGroups()
    {
        var perProvider = new List<(IDeviceBackendProvider P, IReadOnlyList<DeviceDescriptor> List)>();
        foreach (var p in Providers)
        {
            try { perProvider.Add((p, p.Discover())); }
            catch (Exception ex)
            {
                _log.Warn($"{p.Technique}: no se pudo listar dispositivos ({ex.Message})");
                perProvider.Add((p, Array.Empty<DeviceDescriptor>()));
            }
        }

        var groups = new List<List<Member>>();
        foreach (var (p, list) in perProvider)
        {
            foreach (var d in list)
            {
                var g = groups.FirstOrDefault(x => x.Any(m => m.Provider != p && m.Device.IsSameDevice(d)));
                if (g != null) g.Add(new Member(p, d));
                else groups.Add(new List<Member> { new(p, d) });
            }
        }

        // Respaldo: si el número de serie no permitió emparejar (p. ej. WPD no dio UDID) pero cada técnica en vivo
        // ve exactamente un dispositivo, se asume que es el mismo aparato.
        var live = perProvider.Where(x => !x.P.IsSnapshot && x.List.Count > 0).ToList();
        if (live.Count >= 2 && live.All(x => x.List.Count == 1))
        {
            var singles = groups.Where(g => g.Count == 1 && !g[0].Provider.IsSnapshot).ToList();
            if (singles.Count == live.Count && singles.Select(g => g[0].Provider).Distinct().Count() == singles.Count)
            {
                for (var i = 1; i < singles.Count; i++)
                {
                    singles[0].AddRange(singles[i]);
                    groups.Remove(singles[i]);
                }
            }
        }

        return groups;
    }

    /// <summary>Dispositivos distintos visibles ahora (un mismo iPhone aparece una sola vez, aunque lo vean varias técnicas).</summary>
    public IReadOnlyList<DeviceDescriptor> DiscoverAll()
    {
        // Dentro de cada grupo se muestra primero la técnica en vivo con mejor prioridad; si solo hay copias, la copia.
        return BuildGroups()
            .Select(g => g.OrderBy(m => m.Provider.IsSnapshot).ThenByDescending(m => m.Provider.Priority).First().Device)
            .ToList();
    }

    /// <summary>
    /// Técnicas aplicables al dispositivo, en orden de preferencia: primero las en vivo (por prioridad),
    /// al final las copias de seguridad.
    /// </summary>
    public IReadOnlyList<Technique> Techniques(DeviceDescriptor target, string? onlyKey = null, bool includeSnapshots = true)
    {
        var group = BuildGroups().FirstOrDefault(g => g.Any(m =>
            m.Device.IsSameDevice(target) ||
            (m.Device.Technique == target.Technique && string.Equals(m.Device.Id, target.Id, StringComparison.OrdinalIgnoreCase))));
        if (group == null) return Array.Empty<Technique>();

        return group
            .Where(m => onlyKey == null || string.Equals(m.Provider.Key, onlyKey, StringComparison.OrdinalIgnoreCase))
            .Where(m => includeSnapshots || !m.Provider.IsSnapshot)
            .OrderBy(m => m.Provider.IsSnapshot)
            .ThenByDescending(m => m.Provider.Priority)
            .Select(m => new Technique(m.Provider, m.Device))
            .ToList();
    }

    /// <summary>Nombres de las técnicas disponibles para un dispositivo (para mostrar en la interfaz).</summary>
    public string DescribeTechniques(DeviceDescriptor target)
    {
        var t = Techniques(target);
        return t.Count == 0 ? "ninguna" : string.Join(", ", t.Select(x => x.Provider.Key + (x.IsSnapshot ? " (copia)" : "")));
    }

    // ------------------------------------------------------------------ Pruebas

    /// <summary>Prueba una técnica contra un dispositivo. No escribe ni borra nada.</summary>
    public async Task<ProbeResult> ProbeAsync(Technique technique, CancellationToken ct, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(60);
        var clock = Stopwatch.StartNew();
        var work = Task.Run(() => ProbeCoreAsync(technique, ct), ct);
        var finished = await Task.WhenAny(work, Task.Delay(limit, ct)).ConfigureAwait(false);
        if (finished == work) return await work.ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        _log.Warn($"{technique.Name}: la prueba tardó más de {limit.TotalSeconds:F0} s y se descarta.");
        return new ProbeResult(technique.Device, technique.Name, false, $"sin respuesta en {limit.TotalSeconds:F0} s",
            0, null, 0, false, null, false, false, clock.Elapsed, 0, ConnectionProblem.Timeout);
    }

    private async Task<ProbeResult> ProbeCoreAsync(Technique technique, CancellationToken ct)
    {
        var provider = technique.Provider;
        IDeviceBackend? backend = null;
        var clock = Stopwatch.StartNew();
        try
        {
            backend = provider.Open(technique.Device);

            var files = backend.EnumerateMedia(ct).Take(60).ToList();
            var listTime = clock.Elapsed;

            var sample = files.Where(f => f.Size > 0).OrderBy(f => f.Size).FirstOrDefault() ?? files.FirstOrDefault();
            var canRead = false;
            double? kbps = null;
            long sampleBytes = 0;
            string? readError = null;

            if (sample != null)
            {
                try
                {
                    var t = Stopwatch.StartNew();
                    using var hs = new HashingWriteStream(null);
                    await backend.CopyToAsync(sample, hs, ct).ConfigureAwait(false);
                    t.Stop();
                    sampleBytes = hs.BytesWritten;
                    canRead = sampleBytes > 0 && (sample.Size <= 0 || sampleBytes == sample.Size);
                    if (t.Elapsed.TotalSeconds > 0) kbps = sampleBytes / 1024.0 / t.Elapsed.TotalSeconds;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { readError = ex.Message; }
            }

            var datesOk = files.Count > 0 && files.Any(f => f.Modified.HasValue);
            var sizesOk = files.Count > 0 && files.Any(f => f.Size > 0);

            var score = provider.Priority
                        + (files.Count > 0 ? 20 : 0)
                        + (canRead ? 30 : 0)
                        + (datesOk ? 5 : 0)
                        + Math.Min((kbps ?? 0) / 1024.0, 40);

            var problem = files.Count == 0 ? ConnectionProblem.NoPhotosVisible
                : !canRead ? ConnectionProblem.ReadFailed : ConnectionProblem.None;
            return new ProbeResult(backend.Device, provider.Technique, true, readError, files.Count, sample?.RelativePath,
                sampleBytes, canRead, kbps, datesOk, sizesOk, listTime, score, problem);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ProbeResult(technique.Device, provider.Technique, false, ex.Message, 0, null, 0, false, null, false, false,
                clock.Elapsed, 0, ConnectionAssistant.Classify(ex));
        }
        finally
        {
            backend?.Dispose();
        }
    }

    /// <summary>
    /// Prueba todas las técnicas en vivo disponibles y las devuelve ordenadas de mejor a peor
    /// (las que fallan, al final; las copias de seguridad, siempre detrás de las en vivo).
    /// </summary>
    public async Task<(IReadOnlyList<Technique> Ordered, IReadOnlyList<ProbeResult> Report)> RankAsync(
        DeviceDescriptor target, string? onlyKey, CancellationToken ct)
    {
        var all = Techniques(target, onlyKey);
        var live = all.Where(t => !t.IsSnapshot).ToList();
        var snapshots = all.Where(t => t.IsSnapshot).ToList();
        if (live.Count <= 1) return (all, Array.Empty<ProbeResult>());

        var report = new List<ProbeResult>();
        foreach (var t in live) report.Add(await ProbeAsync(t, ct).ConfigureAwait(false));

        var ordered = live
            .OrderByDescending(t => report.First(r => r.Technique == t.Name).Score)
            .Concat(snapshots)
            .ToList();
        foreach (var r in report)
            _log.Info($"Prueba {r.Technique}: {(r.Opened ? (r.CanReadSample ? "correcta" : "abre pero no lee") : "no abre")}" +
                      (r.KiloBytesPerSecond is { } k ? $", {k / 1024.0:F1} MB/s" : "") +
                      (string.IsNullOrEmpty(r.Error) ? "" : $" ({r.Error})"));
        return (ordered, report);
    }

    // ------------------------------------------------------------------ Abrir

    /// <summary>
    /// Abre (en solo lectura) la primera técnica que funcione, en orden de preferencia. Las copias de seguridad solo
    /// se usan si no hay ninguna en vivo para ese dispositivo.
    /// </summary>
    public async Task<(IDeviceBackend Backend, IReadOnlyList<ProbeResult> Report)> OpenBestAsync(
        DeviceDescriptor target, bool probe, CancellationToken ct, string? onlyKey = null)
    {
        IReadOnlyList<Technique> candidates;
        IReadOnlyList<ProbeResult> report = Array.Empty<ProbeResult>();

        if (probe)
            (candidates, report) = await RankAsync(target, onlyKey, ct).ConfigureAwait(false);
        else
            candidates = Techniques(target, onlyKey);

        if (candidates.Count == 0)
        {
            throw new DeviceLostException(
                $"No se encuentra '{target.DisplayName}'. ¿Está conectado, desbloqueado y con \"Confiar\" aceptado?",
                problem: ConnectionProblem.NotVisible);
        }

        Exception? last = null;
        foreach (var t in candidates)
        {
            try
            {
                var backend = t.Provider.Open(t.Device);
                _log.Info($"Técnica elegida: {t.Name}");
                return (backend, report);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                _log.Warn($"{t.Name} no pudo abrir el dispositivo: {ex.Message}");
            }
        }

        throw new DeviceLostException("Ninguna técnica pudo abrir el dispositivo: " + last?.Message, last, ConnectionAssistant.Classify(last));
    }

    // ------------------------------------------------------------------ Informe

    /// <summary>
    /// Texto legible con el resultado de las pruebas (para compartirlo o guardarlo). No incluye el número de serie/UDID, el
    /// nombre del dispositivo (suele ser el nombre de su dueño) ni nombres de archivo: se puede enviar tal cual.
    /// </summary>
    public static string FormatReport(DriverStatus driver, IEnumerable<ProbeResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Informe de conexión ===");
        sb.AppendLine($"Fecha: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Controlador de Apple: {(driver.Installed ? (driver.Running ? "activo" : "instalado, parado") : "NO detectado")} — {driver.Message}");
        sb.AppendLine();
        var any = false;
        foreach (var r in results)
        {
            any = true;
            sb.AppendLine($"Dispositivo : {PublicName(r.Device)}");
            sb.AppendLine($"Técnica     : {r.Technique}{(r.Device.IsSnapshot ? "  [copia de seguridad: foto fija de otro momento]" : "")}");
            sb.AppendLine($"Abre        : {(r.Opened ? "sí" : "NO")}");
            if (!string.IsNullOrEmpty(r.Error)) sb.AppendLine($"Error       : {r.Error}");
            sb.AppendLine($"Archivos    : {r.FilesSeen}{(r.FilesSeen >= 60 ? "+ (muestra limitada a 60)" : "")}  (listado en {r.ListTime.TotalSeconds:F1} s)");
            sb.AppendLine($"Lee muestra : {(r.CanReadSample ? "sí" : "NO")}  {(r.SampleFile != null ? $"[{r.SampleBytes} bytes]" : "")}");
            if (r.KiloBytesPerSecond is { } k) sb.AppendLine($"Velocidad   : {k / 1024.0:F2} MB/s (en un archivo pequeño; orientativo)");
            sb.AppendLine($"Informa fecha/tamaño: {(r.ReportsDates ? "fecha sí" : "fecha NO")}, {(r.ReportsSizes ? "tamaño sí" : "tamaño NO")}");
            sb.AppendLine($"Puntuación  : {r.Score:F0}");
            sb.AppendLine();
        }
        if (!any) sb.AppendLine("No se ha podido probar ningún dispositivo.");
        return sb.ToString();
    }

    /// <summary>Cómo se nombra un dispositivo en informes compartibles: solo el modelo (p. ej. iPhone14,5), nunca su nombre ni su serie.</summary>
    public static string PublicName(DeviceDescriptor d) =>
        !string.IsNullOrWhiteSpace(d.Model) ? d.Model : d.IsSnapshot ? "iPhone/iPad (copia de seguridad)" : "iPhone/iPad";
}
