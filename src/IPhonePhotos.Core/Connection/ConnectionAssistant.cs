using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Connection;

/// <summary>Causa probable de que no se pueda leer el dispositivo.</summary>
public enum ConnectionProblem
{
    None,
    DriverMissing,
    NotVisible,
    Locked,
    TrustPending,
    TrustDenied,
    /// <summary>La técnica abre pero no enseña ninguna foto (típico de WPD con el iPhone bloqueado o sin «Confiar»).</summary>
    NoPhotosVisible,
    /// <summary>Se desconectó a mitad y no volvió.</summary>
    Disconnected,
    ReadFailed,
    Timeout,
    Unknown
}

/// <summary>Un fallo de conexión con su causa ya clasificada (lo lanzan las técnicas cuando saben por qué fallan).</summary>
public sealed class ConnectionProblemException : InvalidOperationException
{
    public ConnectionProblemException(ConnectionProblem problem, string message, Exception? inner = null) : base(message, inner)
        => Problem = problem;

    public ConnectionProblem Problem { get; }
}

/// <summary>Pasos del asistente, en el orden en que se comprueban.</summary>
public enum ConnectionStep { Driver, Cable, Unlocked, Trust, Technique }

public enum StepState { Ok, Failed, Pending, Warning }

public sealed record StepStatus(ConnectionStep Step, StepState State);

/// <summary>Resultado del asistente: listo, o el primer paso que falla con UNA causa y la fila de la guía que la explica.</summary>
public sealed record ConnectionDiagnosis(bool Ready, ConnectionStep? FailedStep, ConnectionProblem Problem, IReadOnlyList<StepStatus> Steps)
{
    public GuideRow? Row => Ready ? null : ConnectionGuide.Row(ConnectionGuide.RowIdFor(Problem));
}

/// <summary>
/// Decide, sin interfaz, en qué paso está la conexión: Controlador de Apple → cable/datos → iPhone desbloqueado → «Confiar» →
/// técnica. La ventana solo lo muestra; así se prueba con dispositivos simulados.
/// </summary>
public static class ConnectionAssistant
{
    public static readonly IReadOnlyList<string> StepTitles = new[]
    {
        "Controlador de Apple", "Cable de datos", "iPhone desbloqueado", "«Confiar en este ordenador»", "Lectura de fotos"
    };

    /// <summary>Causa de una excepción de una técnica (recorre las excepciones internas).</summary>
    public static ConnectionProblem Classify(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case ConnectionProblemException c: return c.Problem;
                case DeviceLostException d when d.Problem != ConnectionProblem.None: return d.Problem;
                case TimeoutException: return ConnectionProblem.Timeout;
            }
        }
        return ConnectionProblem.Unknown;
    }

    /// <summary>De varias causas (una por técnica), la que más ayuda a arreglarlo: la más temprana en el orden de pasos.</summary>
    public static ConnectionProblem MostRelevant(IEnumerable<ConnectionProblem> problems)
    {
        var order = new[]
        {
            ConnectionProblem.DriverMissing, ConnectionProblem.NotVisible, ConnectionProblem.Locked, ConnectionProblem.TrustDenied,
            ConnectionProblem.TrustPending, ConnectionProblem.NoPhotosVisible, ConnectionProblem.Disconnected,
            ConnectionProblem.ReadFailed, ConnectionProblem.Timeout, ConnectionProblem.Unknown
        };
        var set = problems.ToHashSet();
        return order.FirstOrDefault(set.Contains, ConnectionProblem.Unknown);
    }

    /// <param name="liveDeviceVisible">Alguna técnica en vivo (no copia de seguridad) ve un dispositivo.</param>
    /// <param name="probes">Pruebas de las técnicas en vivo de ese dispositivo (vacío si no se ve ninguno).</param>
    public static ConnectionDiagnosis Diagnose(DriverStatus driver, bool liveDeviceVisible, IReadOnlyList<ProbeResult> probes)
    {
        var driverState = driver.Running ? StepState.Ok : StepState.Warning;
        if (probes.Any(p => p.Opened && p.FilesSeen > 0 && p.CanReadSample))
            return new(true, null, ConnectionProblem.None, Build(null, driverState));

        var problems = probes.Select(p => p.Problem).Where(p => p != ConnectionProblem.None).ToList();
        ConnectionProblem problem;
        if (problems.Contains(ConnectionProblem.DriverMissing) || (!driver.Running && !liveDeviceVisible))
            problem = ConnectionProblem.DriverMissing;
        else if (!liveDeviceVisible) problem = ConnectionProblem.NotVisible;
        else problem = problems.Count == 0 ? ConnectionProblem.Unknown : MostRelevant(problems);

        var step = problem switch
        {
            ConnectionProblem.DriverMissing => ConnectionStep.Driver,
            ConnectionProblem.NotVisible => ConnectionStep.Cable,
            ConnectionProblem.Locked or ConnectionProblem.NoPhotosVisible => ConnectionStep.Unlocked,
            ConnectionProblem.TrustPending or ConnectionProblem.TrustDenied => ConnectionStep.Trust,
            _ => ConnectionStep.Technique
        };
        return new(false, step, problem, Build(step, step == ConnectionStep.Driver ? StepState.Failed : driverState));
    }

    private static List<StepStatus> Build(ConnectionStep? failed, StepState driverState)
        => Enum.GetValues<ConnectionStep>().Select(s => new StepStatus(s,
            s == ConnectionStep.Driver ? driverState
            : failed == null || s < failed ? StepState.Ok
            : s == failed ? StepState.Failed
            : StepState.Pending)).ToList();
}
