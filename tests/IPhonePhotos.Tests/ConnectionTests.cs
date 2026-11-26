using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Connection;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Transfer;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>Asistente de conexión, guía y errores en lenguaje llano (v1.6), con dispositivos simulados.</summary>
public class ConnectionTests
{
    private static readonly DriverStatus DriverOk = new(true, true, "ok");
    private static readonly DriverStatus NoDriver = new(false, false, "no");

    private static async Task<ConnectionDiagnosis> DiagnoseAsync(DriverStatus driver, params FlakyProvider[] providers)
    {
        var selector = new BackendSelector(providers, new ActivityLog());
        var device = selector.DiscoverAll().FirstOrDefault(d => !d.IsSnapshot);
        var probes = new List<ProbeResult>();
        if (device != null)
            foreach (var t in selector.Techniques(device, includeSnapshots: false))
                probes.Add(await selector.ProbeAsync(t, default));
        return ConnectionAssistant.Diagnose(driver, device != null, probes);
    }

    [Fact]
    public void Guide_has_unique_rows_with_cause_and_action_and_the_required_symptoms()
    {
        var ids = ConnectionGuide.Troubles.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ConnectionGuide.Troubles, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Symptom));
            Assert.False(string.IsNullOrWhiteSpace(r.Cause));
            Assert.False(string.IsNullOrWhiteSpace(r.Action));
        });
        foreach (var id in new[] { "pc-no-ve", "no-confiar", "faltan-fotos", "copia-se-corta", "sin-apple-devices", "antivirus", "sigue-sin-funcionar" })
            Assert.Contains(id, ids);
        Assert.Contains("Restablecer ubicación y privacidad", ConnectionGuide.Row("no-confiar").Action);
        Assert.Equal(GuideAction.OpenStore, ConnectionGuide.Row("sin-apple-devices").Button);
        Assert.Equal(GuideAction.Diagnostic, ConnectionGuide.Row("sigue-sin-funcionar").Button);
        Assert.Equal(6, ConnectionGuide.Checklist.Count);
        Assert.Single(ConnectionGuide.Checklist, c => c.DetectedByApp);
        // Las rutas de Ajustes de iOS avisan de que el nombre cambia según la versión.
        Assert.All(ConnectionGuide.Troubles.Where(r => r.Action.Contains("Ajustes")), r => Assert.Contains(ConnectionGuide.IosNamesVary, r.Action));
    }

    [Theory]
    [InlineData(ConnectionProblem.DriverMissing, ConnectionStep.Driver, "sin-apple-devices")]
    [InlineData(ConnectionProblem.Locked, ConnectionStep.Unlocked, "bloqueado")]
    [InlineData(ConnectionProblem.NoPhotosVisible, ConnectionStep.Unlocked, "bloqueado")]
    [InlineData(ConnectionProblem.TrustPending, ConnectionStep.Trust, "confiar")]
    [InlineData(ConnectionProblem.TrustDenied, ConnectionStep.Trust, "no-confiar")]
    [InlineData(ConnectionProblem.ReadFailed, ConnectionStep.Technique, "sigue-sin-funcionar")]
    [InlineData(ConnectionProblem.Timeout, ConnectionStep.Technique, "sigue-sin-funcionar")]
    [InlineData(ConnectionProblem.Unknown, ConnectionStep.Technique, "sigue-sin-funcionar")]
    public void Every_assistant_error_state_links_to_its_guide_row(ConnectionProblem problem, ConnectionStep step, string row)
    {
        var device = new DeviceDescriptor("x", "x", "", "", "", "t");
        var probe = new ProbeResult(device, "t", false, "e", 0, null, 0, false, null, false, false, TimeSpan.Zero, 0, problem);
        var d = ConnectionAssistant.Diagnose(DriverOk, true, new[] { probe });

        Assert.False(d.Ready);
        Assert.Equal(step, d.FailedStep);
        Assert.Equal(row, d.Row!.Id);
        Assert.Same(ConnectionGuide.Row(row), d.Row); // mismo texto que la guía: una sola fuente
        Assert.Equal(StepState.Failed, d.Steps.Single(s => s.Step == step).State);
        Assert.All(d.Steps.Where(s => s.Step > step), s => Assert.Equal(StepState.Pending, s.State));
    }

    [Fact]
    public void Every_problem_maps_to_an_existing_row()
    {
        foreach (var p in Enum.GetValues<ConnectionProblem>())
            Assert.NotNull(ConnectionGuide.Row(ConnectionGuide.RowIdFor(p)));
    }

    [Fact]
    public async Task No_driver_and_nothing_visible_points_to_the_driver_first()
    {
        using var sb = new Sandbox();
        var gone = new FlakyProvider(sb.Source) { Present = false, ReappearAfterPolls = int.MaxValue };
        var d = await DiagnoseAsync(NoDriver, gone);
        Assert.Equal(ConnectionStep.Driver, d.FailedStep);
        Assert.Equal("sin-apple-devices", d.Row!.Id);
    }

    [Fact]
    public async Task Driver_ok_and_nothing_visible_points_to_the_cable()
    {
        using var sb = new Sandbox();
        var gone = new FlakyProvider(sb.Source) { Present = false, ReappearAfterPolls = int.MaxValue };
        var d = await DiagnoseAsync(DriverOk, gone);
        Assert.Equal(ConnectionStep.Cable, d.FailedStep);
        Assert.Equal(StepState.Ok, d.Steps[0].State);
    }

    [Fact]
    public async Task Locked_and_denied_trust_are_told_apart_and_locked_wins_as_the_earlier_step()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(2);
        var denied = await DiagnoseAsync(DriverOk, new FlakyProvider(sb.Source) { OpenProblem = ConnectionProblem.TrustDenied });
        Assert.Equal("no-confiar", denied.Row!.Id);

        var both = await DiagnoseAsync(DriverOk,
            new FlakyProvider(sb.Source, "A", "a") { OpenProblem = ConnectionProblem.TrustDenied },
            new FlakyProvider(sb.Source, "B", "b") { OpenProblem = ConnectionProblem.Locked });
        Assert.Equal(ConnectionStep.Unlocked, both.FailedStep);
    }

    [Fact]
    public async Task Technique_that_opens_but_shows_nothing_means_unlock_and_trust()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(2);
        var d = await DiagnoseAsync(DriverOk, new FlakyProvider(sb.Source) { ShowNothing = true });
        Assert.Equal(ConnectionProblem.NoPhotosVisible, d.Problem);
        Assert.Contains("Desbloquea el iPhone y pulsa «Confiar»", d.Row!.Action);
    }

    [Fact]
    public async Task Working_device_is_ready_even_if_the_driver_check_is_unsure()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(2);
        var d = await DiagnoseAsync(NoDriver, new FlakyProvider(sb.Source));
        Assert.True(d.Ready);
        Assert.Null(d.Row);
        Assert.Equal(StepState.Warning, d.Steps[0].State); // el estado real del controlador se enseña, sin bloquear
    }

    [Fact]
    public async Task Session_that_cannot_open_reports_the_most_useful_cause()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(2);
        var log = new ActivityLog();
        var selector = new BackendSelector(new[]
        {
            new FlakyProvider(sb.Source, "A", "a") { OpenProblem = ConnectionProblem.ReadFailed },
            new FlakyProvider(sb.Source, "B", "b") { OpenProblem = ConnectionProblem.Locked }
        }, log);
        var ex = await Assert.ThrowsAsync<DeviceLostException>(() => new TransferSession(selector, log)
            .RunAsync(selector.DiscoverAll().Single(), new TransferOptions { DestinationRoot = sb.Dest, AllowSnapshotFallback = false }, null, null, default));
        Assert.Equal(ConnectionProblem.Locked, ex.Problem);

        var user = ErrorExplainer.Explain(ex);
        Assert.Equal("bloqueado", user.GuideRowId);
        Assert.Contains("no abre", user.Details); // lo técnico, solo en «Detalles»
        Assert.DoesNotContain("no abre", user.Cause + user.Action);
    }

    [Fact]
    public void Plain_errors_for_disk_full_permissions_and_unknown()
    {
        var full = ErrorExplainer.Explain(new IOException("disk", unchecked((int)0x80070070)));
        Assert.Contains("espacio", full.Cause);
        var denied = ErrorExplainer.Explain(new UnauthorizedAccessException("C:\\x"));
        Assert.Contains("carpeta de destino", denied.Cause);
        Assert.Contains("C:\\x", denied.Details);
        var unknown = ErrorExplainer.Explain(new InvalidOperationException("raro"));
        Assert.Equal("sigue-sin-funcionar", unknown.GuideRowId);
        Assert.Contains("Reintentar", unknown.Action);
        Assert.Equal("Llegó incompleto (tamaño distinto)", ErrorExplainer.FileReason(new IncompleteFileException(1, 2)));
    }
}
