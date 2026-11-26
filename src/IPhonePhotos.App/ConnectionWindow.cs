using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Connection;

namespace IPhonePhotos.App;

/// <summary>
/// «Conectar tu iPhone o iPad»: el asistente (estado en vivo, una causa y una acción, reintento automático) y la guía fija
/// (qué preparar en el teléfono y solución de problemas). Todos los textos salen de <see cref="ConnectionGuide"/>; la decisión
/// la toma <see cref="ConnectionAssistant"/> en Core. Solo lee del dispositivo.
/// </summary>
public sealed class ConnectionWindow : Window
{
    private static readonly TimeSpan AutoRetry = TimeSpan.FromSeconds(4);
    private readonly BackendSelector _selector = new(App.CreateProviders(includeBackup: false), new ActivityLog());
    private readonly Func<bool> _isBusy;
    private readonly DispatcherTimer _timer = new() { Interval = AutoRetry };
    private readonly StackPanel _steps = new();
    private readonly TextBlock _cause = new() { FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _action = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _auto = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _retry = new() { Content = "Reintentar", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _store = new() { Content = "Abrir Microsoft Store", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _diag = new() { Content = "Diagnóstico" };
    private readonly TextBlock _driverState = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Dictionary<string, Border> _rows = new();
    private bool _checking;
    private DriverStatus _driver = new(false, false, "");
    private IReadOnlyList<ProbeResult> _probes = Array.Empty<ProbeResult>();

    /// <param name="focusRowId">Fila de la guía que se resalta al abrir (la del error que ha llevado aquí).</param>
    public ConnectionWindow(Func<bool>? isBusy = null, string? focusRowId = null)
    {
        _isBusy = isBusy ?? (() => false);
        Title = "Conectar tu iPhone o iPad";
        Width = 860; Height = 900; MinWidth = 560; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PageBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        FontSize = 14;

        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };
        root.Children.Add(new TextBlock { Text = "Conectar tu iPhone o iPad", Style = (Style)FindResource("LargeTitle") });
        root.Children.Add(Muted("La app solo lee del teléfono: nunca escribe ni borra nada en él. Nada de esta guía se ha probado todavía con un iPhone real."));

        // ---- Asistente
        root.Children.Add(Header("ESTADO DE LA CONEXIÓN"));
        var state = new StackPanel();
        state.Children.Add(_steps);
        state.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
        state.Children.Add(_cause);
        state.Children.Add(_action);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _retry.Style = (Style)FindResource("PrimaryButton");
        foreach (var b in new[] { _retry, _store, _diag }) { if (b != _retry) b.Style = (Style)FindResource("SecondaryButton"); buttons.Children.Add(b); }
        _retry.Click += async (_, _) => await CheckAsync();
        _store.Click += (_, _) => OpenStore();
        _diag.Click += (_, _) => SaveDiagnostic();
        state.Children.Add(buttons);
        _auto.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        state.Children.Add(_auto);
        root.Children.Add(Card(state));

        // ---- Antes de conectar
        root.Children.Add(Header("ANTES DE CONECTAR"));
        var checks = new StackPanel();
        foreach (var c in ConnectionGuide.Checklist)
        {
            var row = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            var box = new CheckBox { Content = c.Title, FontWeight = FontWeights.SemiBold };
            AutomationProperties.SetName(box, $"{c.Title}: comprobado (solo para ti, la app no lo guarda)");
            AutomationProperties.SetHelpText(box, c.Text);
            row.Children.Add(box);
            if (c.DetectedByApp) row.Children.Add(_driverState);
            row.Children.Add(new TextBlock { Text = c.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, 2, 0, 0) });
            checks.Children.Add(row);
        }
        checks.Children.Add(Muted("Las casillas «Comprobado» son solo para ayudarte a repasar; la app no las usa."));
        root.Children.Add(Card(checks));

        // ---- Solución de problemas
        root.Children.Add(Header("SOLUCIÓN DE PROBLEMAS"));
        var table = new StackPanel();
        foreach (var r in ConnectionGuide.Troubles) table.Children.Add(TroubleRow(r));
        root.Children.Add(Card(table));

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _timer.Tick += async (_, _) => await CheckAsync(auto: true);
        Loaded += async (_, _) =>
        {
            if (focusRowId != null && _rows.TryGetValue(focusRowId, out var row)) { Highlight(focusRowId); row.BringIntoView(); }
            await CheckAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    // ------------------------------------------------------------------ Comprobación

    private async Task CheckAsync(bool auto = false)
    {
        if (_checking) return;
        if (_isBusy())
        {
            _auto.Text = "Hay una copia en curso: la comprobación automática se pausa para no molestarla.";
            return;
        }
        _checking = true;
        _retry.IsEnabled = false;
        if (!auto) _cause.Text = "Comprobando…";
        try
        {
            var (driver, visible, probes) = await Task.Run(async () =>
            {
                var d = string.IsNullOrEmpty(App.SourceFolderArg) ? AppleDriverCheck.Check() : new DriverStatus(true, true, "Carpeta de pruebas.");
                var device = _selector.DiscoverAll().FirstOrDefault(x => !x.IsSnapshot);
                var list = new List<ProbeResult>();
                if (device != null)
                    foreach (var t in _selector.Techniques(device, includeSnapshots: false))
                        list.Add(await _selector.ProbeAsync(t, CancellationToken.None, TimeSpan.FromSeconds(20)));
                return (d, device != null, list);
            });
            _driver = driver;
            _probes = probes;
            Show(ConnectionAssistant.Diagnose(driver, visible, probes));
        }
        catch (Exception ex)
        {
            var e = ErrorExplainer.Explain(ex);
            _cause.Text = e.Cause;
            _action.Text = e.Action;
        }
        finally
        {
            _checking = false;
            _retry.IsEnabled = true;
        }
    }

    private void Show(ConnectionDiagnosis d)
    {
        _steps.Children.Clear();
        foreach (var s in d.Steps)
        {
            var (glyph, word, brush) = s.State switch
            {
                StepState.Ok => ("✓", "correcto", "Success"),
                StepState.Failed => ("✗", "falla aquí", "Danger"),
                StepState.Warning => ("!", "no detectado (se intenta igualmente)", "Label"), // naranja sobre blanco no da contraste suficiente
                _ => ("·", "pendiente", "Muted")
            };
            var title = ConnectionAssistant.StepTitles[(int)s.Step];
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var mark = new TextBlock { Text = glyph, Width = 22, FontWeight = FontWeights.Bold };
            mark.SetResourceReference(TextBlock.ForegroundProperty, brush);
            line.Children.Add(mark);
            line.Children.Add(new TextBlock { Text = $"{title} — {word}" });
            AutomationProperties.SetName(line, $"{title}: {word}");
            _steps.Children.Add(line);
        }

        _driverState.Text = _driver.Running ? "✓ La app detecta el servicio de Apple en marcha." : "✗ " + _driver.Message;
        _driverState.SetResourceReference(TextBlock.ForegroundProperty, _driver.Running ? "Success" : "Danger");

        if (d.Ready)
        {
            _cause.Text = "✓ Todo listo: la app puede leer las fotos del teléfono.";
            _action.Text = "Cierra esta ventana y pulsa «Descargar».";
            _timer.Stop();
            _auto.Text = "";
            Highlight(null);
        }
        else
        {
            var row = d.Row!;
            _cause.Text = row.Cause;
            _action.Text = row.Action;
            _auto.Text = $"Se vuelve a comprobar sola cada {AutoRetry.TotalSeconds:F0} segundos mientras esta ventana esté abierta. " +
                         $"Más ayuda en «{row.Symptom}», abajo.";
            if (!_timer.IsEnabled && IsLoaded) _timer.Start();
            Highlight(row.Id);
        }
        _store.Visibility = d.Problem == ConnectionProblem.DriverMissing ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_cause, _cause.Text);
    }

    // ------------------------------------------------------------------ Acciones

    public static void OpenStore()
    {
        try { Process.Start(new ProcessStartInfo(ConnectionGuide.StoreSearchUri) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo abrir Microsoft Store. Ábrela tú y busca «Apple Devices».\n\nDetalles: " + ex.Message,
                "IOS Photos", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Guarda el informe de <c>probe</c> (sin serie ni nombre del teléfono ni archivos).</summary>
    private void SaveDiagnostic()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar el diagnóstico de conexión",
            FileName = "informe-conexion.txt",
            Filter = "Texto (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, BackendSelector.FormatReport(_driver, _probes), System.Text.Encoding.UTF8);
            MessageBox.Show(this, "Informe guardado. No incluye el nombre ni el número de serie del teléfono.", "IOS Photos",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            var e = ErrorExplainer.Explain(ex);
            MessageBox.Show(this, $"{e.Cause}\n{e.Action}\n\nDetalles: {e.Details}", "IOS Photos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ Piezas

    private Border TroubleRow(GuideRow r)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = r.Symptom, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Causa: " + r.Cause, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "Qué hacer: " + r.Action, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Button? button = r.Button switch
        {
            GuideAction.OpenStore => new Button { Content = "Abrir Microsoft Store" },
            GuideAction.Diagnostic => new Button { Content = "Diagnóstico" },
            _ => null
        };
        if (button != null)
        {
            button.Style = (Style)FindResource("SecondaryButton");
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 8, 0, 0);
            button.Click += (_, _) => { if (r.Button == GuideAction.OpenStore) OpenStore(); else SaveDiagnostic(); };
            panel.Children.Add(button);
        }
        var border = new Border { Child = panel, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 2, 0, 2), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2) };
        border.BorderBrush = Brushes.Transparent;
        AutomationProperties.SetName(border, $"{r.Symptom}. Causa: {r.Cause}. Qué hacer: {r.Action}");
        _rows[r.Id] = border;
        return border;
    }

    private void Highlight(string? id)
    {
        foreach (var (key, b) in _rows)
        {
            if (key == id) { b.SetResourceReference(Border.BorderBrushProperty, "Accent"); b.SetResourceReference(Border.BackgroundProperty, "AccentTint"); }
            else { b.BorderBrush = Brushes.Transparent; b.Background = Brushes.Transparent; }
        }
    }

    private TextBlock Header(string text) => new() { Text = text, Style = (Style)FindResource("SectionHeader"), Margin = new Thickness(16, 18, 0, 6) };

    private Border Card(UIElement child) => new() { Style = (Style)FindResource("Card"), Padding = new Thickness(16, 12, 16, 12), Child = child };

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }
}
