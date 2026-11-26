using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Connection;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Organize;
using IPhonePhotos.Core.Transfer;
using LogLevel = IPhonePhotos.Core.LogLevel;

namespace IPhonePhotos.App;

public partial class MainWindow : Window
{
    private readonly ActivityLog _log = new();
    private readonly BackendSelector _selector;
    private readonly PortableSettings _settings = PortableSettings.Load();
    private readonly ObservableCollection<string> _logItems = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? _cts;
    private IDisposable? _gate;
    private bool _busy;
    private bool _refreshing;
    private bool _interrupted;

    /// <summary>Hay una copia, comparación o verificación en curso (la ayuda de conexión no prueba el dispositivo mientras tanto).</summary>
    public bool IsBusy => _busy;
    private int _lastDone, _lastTotal;
    private IReadOnlyList<DeviceDescriptor> _devices = Array.Empty<DeviceDescriptor>();

    public MainWindow()
    {
        InitializeComponent();

        // Todas las técnicas: el selector las prueba y usa la mejor; si una falla, pasa a la siguiente.
        _selector = new BackendSelector(App.CreateProviders(includeBackup: true), _log);

        LogList.ItemsSource = _logItems;
        _log.Entry += OnLogEntry;

        DestBox.Text = App.DestArg ?? _settings.Destination ??
                       Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iPhone");
        AdoptCombo.SelectedIndex = _settings.Adopt switch { AdoptMode.Deep => 1, AdoptMode.None => 2, _ => 0 };
        VerifyDeepCheck.IsChecked = _settings.VerifyDeep;
        SnapshotCheck.IsChecked = _settings.AllowSnapshotFallback;
        OrganizeCheck.IsChecked = _settings.OrganizeByDate;
        NewFolderCheck.IsChecked = _settings.UseNewFolder;
        JpgCheck.IsChecked = _settings.AlsoJpg;
        TechniqueCombo.SelectedIndex = _settings.Technique switch { "wpd" => 1, "afc" => 2, "backup" => 3, _ => 0 };

        UpdateDriverBanner();
        Loaded += async (_, _) =>
        {
            await RefreshDevicesAsync();
            _poll.Tick += async (_, _) => { if (!_busy) await RefreshDevicesAsync(); };
            _poll.Start();
        };
        Closing += OnClosing;
        UpdateButtons();
    }

    // ------------------------------------------------------------------ Dispositivos

    private void UpdateDriverBanner()
    {
        if (!string.IsNullOrEmpty(App.SourceFolderArg)) { DriverBanner.Visibility = Visibility.Collapsed; return; }
        var s = AppleDriverCheck.Check();
        DriverBanner.Visibility = s.Running ? Visibility.Collapsed : Visibility.Visible;
        DriverText.Text = s.Message;
    }

    private async Task RefreshDevicesAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var previous = DeviceCombo.SelectedItem as DeviceDescriptor;
            var found = await Task.Run(() => _selector.DiscoverAll());

            // Solo se toca la lista si ha cambiado, para no molestar a quien está eligiendo.
            var same = found.Count == _devices.Count && found.Zip(_devices).All(p => p.First.Id == p.Second.Id);
            if (!same)
            {
                _devices = found;
                DeviceCombo.ItemsSource = found;
                DeviceCombo.SelectedItem = previous != null
                    ? found.FirstOrDefault(d => d.IsSameDevice(previous))
                    : null;
                if (DeviceCombo.SelectedItem == null && found.Count > 0) DeviceCombo.SelectedIndex = 0;
            }

            DeviceHint.Text = found.Count == 0
                ? "No se ve ningún dispositivo: usa un cable de datos, desbloquea el iPhone y pulsa «Confiar». Si sigue sin aparecer, pulsa «Ayuda de conexión»."
                : $"{found.Count} dispositivo(s) disponible(s).";
            if (DeviceCombo.SelectedItem is DeviceDescriptor sel)
            {
                var techniques = await Task.Run(() => _selector.DescribeTechniques(sel));
                DeviceHint.Text += $" Técnicas para este dispositivo: {techniques}." +
                                   (sel.IsSnapshot ? " Es una copia de seguridad: puede faltar lo más reciente." : "");
            }
            if (!_busy && found.Count == 0) StatusText.Text = "Conecta el iPhone con el cable, desbloquéalo y pulsa «Confiar».";
            UpdateDriverBanner();
        }
        finally
        {
            _refreshing = false;
            UpdateButtons();
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateButtons();

    /// <summary>Tras la primera copia en esta carpeta, la acción por defecto es «Copiar solo lo nuevo» (lo ya copiado nunca se repite).</summary>
    private void UpdateDownloadLabel()
    {
        if (_interrupted) { DownloadButton.Content = "Continuar"; return; }
        var dest = DestBox.Text.Trim();
        bool previous;
        try { previous = dest.Length > 0 && CopyReport.HasPreviousCopy(Path.GetFullPath(dest)); } catch { previous = false; }
        DownloadButton.Content = previous ? "Copiar solo lo nuevo" : "Descargar";
        AutomationProperties.SetHelpText(DownloadButton, previous
            ? "Ya hay una copia en esta carpeta: solo se copian las fotos que falten."
            : "Copia todas las fotos y vídeos. Si se corta, continúa donde lo dejó.");
    }

    // ------------------------------------------------------------------ Acciones

    private async void OnDownload(object sender, RoutedEventArgs e) => await RunDownloadAsync(null);

    /// <param name="only">Si no es null, solo se reintentan esas rutas del dispositivo (lista «no copiados»).</param>
    private async Task RunDownloadAsync(IReadOnlySet<string>? only)
    {
        if (!TryGetContext(out var device, out var options)) return;
        options.OnlyPaths = only;
        SaveSettings();
        if (!BeginBusy("descargar")) return;
        HideResult();
        _lastDone = _lastTotal = 0;
        try
        {
            var progress = new Progress<TransferProgress>(OnProgress);
            var status = new Progress<string>(s => StatusText.Text = s);
            var summary = await Task.Run(() =>
                new TransferSession(_selector, _log).RunAsync(device, options, progress, status, _cts!.Token));

            _interrupted = false;
            StatusText.Text = summary.Failed == 0 ? "Copia terminada." : $"Copia terminada: {summary.Failed} archivo(s) no se pudieron copiar.";
            Bar.IsIndeterminate = false;
            Bar.Value = 100;
            // El mismo texto que ultima-copia.txt (más la técnica, que solo se enseña aquí).
            SummaryText.Text = (summary.ReportText ?? "").TrimEnd() +
                               (summary.TechniquesUsed.Count > 0 ? $"\nTécnica(s): {string.Join(", ", summary.TechniquesUsed)}" : "");
            AfterPanel.Visibility = Visibility.Visible;
            ShowFailed(summary);
        }
        catch (OperationCanceledException)
        {
            _interrupted = true;
            StatusText.Text = Interrupted("Detenido.");
        }
        catch (Exception ex)
        {
            if (ex is DeviceLostException) _interrupted = _lastTotal > 0;
            if (_interrupted) StatusText.Text = Interrupted("La copia se ha cortado.");
            ShowError(ex, () => RunDownloadAsync(only));
        }
        finally
        {
            EndBusy();
        }
    }

    private string Interrupted(string what) =>
        $"{what} Copiadas {_lastDone} de {_lastTotal} en esta tanda. Lo copiado queda guardado: pulsa «Continuar» y seguirá donde lo dejó.";

    // ------------------------------------------------------------------ No copiados y errores

    private sealed record FailedItem(string Path, string Reason)
    {
        public string RetryName => $"Reintentar {System.IO.Path.GetFileName(Path)}";
    }

    private void ShowFailed(TransferSummary summary)
    {
        var items = summary.FailedFiles
            .Select(f => new FailedItem(f, summary.FailureReasons.TryGetValue(f, out var r) ? r : "Motivo desconocido"))
            .ToList();
        FailedList.ItemsSource = items;
        FailedTitle.Text = $"No copiados ({items.Count}). El resto sí se copió.";
        FailedPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRetryOneFailed(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FailedItem f)
            await RunDownloadAsync(new HashSet<string>(new[] { f.Path }, StringComparer.OrdinalIgnoreCase));
    }

    private async void OnRetryAllFailed(object sender, RoutedEventArgs e)
    {
        if (FailedList.ItemsSource is IEnumerable<FailedItem> all)
            await RunDownloadAsync(all.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private void OnVerifyDeepNow(object sender, RoutedEventArgs e)
    {
        VerifyDeepCheck.IsChecked = true;
        OnVerify(sender, e);
    }

    private Func<Task>? _errorRetry;
    private string? _errorRow;

    /// <summary>Error en lenguaje llano: causa, acción, «Reintentar», «Ayuda de conexión» y lo técnico plegado en «Detalles».</summary>
    private void ShowError(Exception ex, Func<Task> retry)
    {
        var e = ErrorExplainer.Explain(ex);
        _log.Error(ex.Message);
        _errorRetry = retry;
        _errorRow = e.GuideRowId;
        ErrorCause.Text = e.Cause;
        ErrorAction.Text = e.Action;
        ErrorDetails.Text = e.Details;
        ErrorPanel.Visibility = Visibility.Visible;
        if (!_interrupted) StatusText.Text = e.Cause;
        AutomationProperties.SetName(ErrorPanel, $"Error: {e.Cause} {e.Action}");
        ErrorRetry.Focus();
    }

    private void HideResult()
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        AfterPanel.Visibility = Visibility.Collapsed;
        FailedPanel.Visibility = Visibility.Collapsed;
        SummaryText.Text = "";
    }

    private async void OnErrorRetry(object sender, RoutedEventArgs e)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        if (_errorRetry != null) await _errorRetry();
    }

    private void OnErrorHelp(object sender, RoutedEventArgs e) => OpenHelp(_errorRow);

    private void OnConnectionHelp(object sender, RoutedEventArgs e) => OpenHelp(null);

    private void OpenHelp(string? row) => new ConnectionWindow(() => _busy, row) { Owner = this }.Show();

    private void OnStop(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Deteniendo…";
        _cts?.Cancel();
    }

    private async void OnCompare(object sender, RoutedEventArgs e)
    {
        if (!TryGetContext(out var device, out var options)) return;
        if (!BeginBusy("comparar")) return;
        HideResult();
        Bar.IsIndeterminate = true;
        StatusText.Text = "Comparando el dispositivo con lo ya descargado…";
        try
        {
            Directory.CreateDirectory(options.DestinationRoot);
            var scan = await Task.Run(async () =>
            {
                var (backend, _) = await _selector.OpenBestAsync(device, false, _cts!.Token, options.OnlyTechnique);
                using (backend)
                using (var store = ManifestStore.Open(options.DestinationRoot))
                    return new TransferEngine(_log).Scan(backend, store, options,
                        new Progress<string>(s => Dispatcher.BeginInvoke(() => StatusText.Text = s)), _cts!.Token);
            });

            StatusText.Text = "Comparación terminada.";
            SummaryText.Text =
                $"En el dispositivo: {scan.All.Count} archivos ({Format.Bytes(scan.TotalBytes)})\n" +
                $"Ya copiados: {scan.AlreadyDone} ({Format.Bytes(scan.AlreadyDoneBytes)})\n" +
                $"Pendientes: {scan.Pending.Count} ({Format.Bytes(scan.PendingBytes)})" +
                (scan.MissingOnDevice > 0 ? $"\nCopiados pero que ya no están en el dispositivo: {scan.MissingOnDevice}" : "");
            if (scan.All.Count == 0)
                SummaryText.Text += "\nNo se ve ninguna foto: comprueba que el iPhone esté desbloqueado y que hayas pulsado «Confiar».";
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelado."; }
        catch (Exception ex) { ShowError(ex, () => { OnCompare(this, new RoutedEventArgs()); return Task.CompletedTask; }); }
        finally
        {
            Bar.IsIndeterminate = false;
            EndBusy();
        }
    }

    private async void OnVerify(object sender, RoutedEventArgs e)
    {
        if (!TryGetContext(out var device, out var options)) return;
        SaveSettings();
        if (!BeginBusy("verificar")) return;
        HideResult();
        var deep = VerifyDeepCheck.IsChecked == true;
        try
        {
            var progress = new Progress<TransferProgress>(OnProgress);
            var (local, deepResult) = await Task.Run(async () =>
            {
                var (backend, _) = await _selector.OpenBestAsync(device, false, _cts!.Token, options.OnlyTechnique);
                using (backend)
                using (var store = ManifestStore.Open(options.DestinationRoot))
                {
                    var svc = new VerificationService(_log);
                    var l = await svc.VerifyLocalAsync(store, options.DestinationRoot, true, progress, _cts!.Token);
                    VerifyResult? d = deep
                        ? await svc.VerifyDeepAsync(backend, store, options.DestinationRoot, progress, _cts!.Token)
                        : null;
                    return (l, d);
                }
            });

            Bar.Value = 100;
            StatusText.Text = "Verificación terminada.";
            SummaryText.Text = $"En el PC: {local.Verified} correctos, {local.Corrupt} dañados, {local.MissingLocal} que faltan.";
            if (deepResult != null)
                SummaryText.Text += $"\nContrastados con el iPhone: {deepResult.Verified} idénticos, {deepResult.Corrupt} con problemas.";
            if (local.AdoptedUnverified > 0 && deepResult == null)
                SummaryText.Text += $"\n{local.AdoptedUnverified} adoptados sin verificar (ya estaban en la carpeta): marca «a fondo» para compararlos con el iPhone.";
            if (local.Corrupt + local.MissingLocal > 0 || (deepResult?.Corrupt ?? 0) > 0)
                SummaryText.Text += "\nLos que tienen problemas se volverán a descargar aparte al pulsar Descargar (el archivo dudoso no se toca).";
            // Sustituto del antiguo «Liberar espacio»: el informe dice qué está a salvo; borrar del iPhone lo decide el usuario en la app Fotos.
            SummaryText.Text += "\n" + CopyReport.NeverDeletes + "\n" + CopyReport.ICloudDeleteWarning;
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelado."; }
        catch (Exception ex) { ShowError(ex, () => { OnVerify(this, new RoutedEventArgs()); return Task.CompletedTask; }); }
        finally { EndBusy(); }
    }

    // «Organizar» y «Limpiar Live Photos» están en la barra lateral de la ventana principal: aquí solo se descarga y verifica.

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = DestBox.Text.Trim();
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else MessageBox.Show(this, "La carpeta todavía no existe.", "IOS Photos", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta donde guardar las fotos" };
        if (Directory.Exists(DestBox.Text)) dialog.InitialDirectory = DestBox.Text;
        if (dialog.ShowDialog(this) == true) DestBox.Text = dialog.FolderName;
    }

    // ------------------------------------------------------------------ Progreso y estado

    private void OnProgress(TransferProgress p)
    {
        switch (p.Phase)
        {
            case TransferPhase.Scanning:
                Bar.IsIndeterminate = true;
                DetailText.Text = "";
                CurrentText.Text = "";
                return;
            case TransferPhase.Waiting:
                Bar.IsIndeterminate = true;
                DetailText.Text = "Esperando a que el dispositivo vuelva (revisa el cable y desbloquea el iPhone)…";
                return;
        }

        Bar.IsIndeterminate = false;
        if (p.Phase == TransferPhase.Done && p.FilesTotal == 0)
        {
            // Cierre de la sesión (el resumen ya se muestra debajo): no repetir "0 de 0 archivos".
            DetailText.Text = "";
            CurrentText.Text = "";
            return;
        }
        double pct = p.BytesTotal > 0 ? p.BytesDone * 100.0 / p.BytesTotal
                   : p.FilesTotal > 0 ? p.FilesDone * 100.0 / p.FilesTotal : 0;
        Bar.Value = Math.Clamp(pct, 0, 100);

        var verb = p.Phase switch { TransferPhase.Verifying => "verificados", TransferPhase.Organizing => "organizados", _ => "copiados" };
        if (p.Phase == TransferPhase.Downloading) { _lastDone = p.FilesDone; _lastTotal = p.FilesTotal; }
        var eta = "";
        if (p.Phase == TransferPhase.Downloading && p.BytesPerSecond > 0 && p.BytesTotal > p.BytesDone)
            eta = $" · quedan ~{Format.Duration(TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / p.BytesPerSecond))}";
        var speed = p.BytesPerSecond > 0 ? $" · {Format.Bytes((long)p.BytesPerSecond)}/s" : "";
        DetailText.Text = $"{p.FilesDone} de {p.FilesTotal} {verb}{speed}{eta} · {Format.Bytes(p.BytesDone)} de {Format.Bytes(p.BytesTotal)}";
        CurrentText.Text = p.CurrentFile ?? "";
    }

    private void OnLogEntry(LogEntry e) => Dispatcher.BeginInvoke(() =>
    {
        _logItems.Add($"{e.Time:HH:mm:ss}  {(e.Level == LogLevel.Info ? "" : e.Level == LogLevel.Warn ? "[aviso] " : "[ERROR] ")}{e.Message}");
        while (_logItems.Count > 2000) _logItems.RemoveAt(0);
        if (_logItems.Count > 0) LogList.ScrollIntoView(_logItems[^1]);
    });

    /// <summary>Con <paramref name="gateFor"/>, toma el bloqueo común de la carpeta (una operación que escribe a la vez).</summary>
    private bool BeginBusy(string? gateFor)
    {
        if (gateFor != null)
        {
            _gate = LibraryGate.TryEnter(gateFor);
            if (_gate == null) { StatusText.Text = LibraryGate.BusyMessage; return false; }
        }
        _busy = true;
        _cts = new CancellationTokenSource();
        UpdateButtons();
        return true;
    }

    private void EndBusy()
    {
        _gate?.Dispose();
        _gate = null;
        _busy = false;
        _cts?.Dispose();
        _cts = null;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var ready = !_busy && DeviceCombo.SelectedItem is DeviceDescriptor && !string.IsNullOrWhiteSpace(DestBox.Text);
        DownloadButton.IsEnabled = ready;
        CompareButton.IsEnabled = ready;
        VerifyButton.IsEnabled = ready;
        // Organizar y limpiar Live Photos trabajan sobre una carpeta ya descargada: no necesitan el iPhone conectado.
        OrganizeCheck.IsEnabled = !_busy;
        NewFolderCheck.IsEnabled = !_busy;
        TechniqueCombo.IsEnabled = !_busy;
        SnapshotCheck.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy;
        DeviceCombo.IsEnabled = !_busy;
        DestBox.IsEnabled = !_busy;
        AdoptCombo.IsEnabled = !_busy;
        StopButton.IsEnabled = _busy && _cts != null;
        JpgCheck.IsEnabled = !_busy;
        UpdateDownloadLabel();
    }

    private bool TryGetContext(out DeviceDescriptor device, out TransferOptions options)
    {
        device = DeviceCombo.SelectedItem as DeviceDescriptor ?? new DeviceDescriptor("", "", "", "", "", "");
        options = new TransferOptions();
        var dest = DestBox.Text.Trim();
        if (device.Id == "" || string.IsNullOrWhiteSpace(dest)) return false;
        try { dest = Path.GetFullPath(dest); }
        catch
        {
            MessageBox.Show(this, "La carpeta de destino no es válida.", "IOS Photos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        options = new TransferOptions
        {
            DestinationRoot = dest,
            Adopt = SelectedAdopt(),
            OnlyTechnique = SelectedTechnique(),
            OrganizeByDate = OrganizeCheck.IsChecked == true,
            NewFolder = NewFolderCheck.IsChecked == true ? AlbumService.InboxFolder : null,
            AllowSnapshotFallback = SnapshotCheck.IsChecked == true,
            AlsoJpg = JpgCheck.IsChecked == true
        };
        return true;
    }

    private string? SelectedTechnique()
    {
        var tag = (TechniqueCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.IsNullOrEmpty(tag) ? null : tag;
    }

    private AdoptMode SelectedAdopt()
    {
        var tag = (AdoptCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "Deep" => AdoptMode.Deep,
            "None" => AdoptMode.None,
            _ => AdoptMode.Fast
        };
    }

    private void SaveSettings()
    {
        _settings.Destination = DestBox.Text.Trim();
        _settings.Adopt = SelectedAdopt();
        _settings.VerifyDeep = VerifyDeepCheck.IsChecked == true;
        _settings.Technique = SelectedTechnique() ?? "";
        _settings.AllowSnapshotFallback = SnapshotCheck.IsChecked == true;
        _settings.OrganizeByDate = OrganizeCheck.IsChecked == true;
        _settings.UseNewFolder = NewFolderCheck.IsChecked == true;
        _settings.AlsoJpg = JpgCheck.IsChecked == true;
        _settings.Save();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            var answer = MessageBox.Show(this,
                "Hay una operación en curso. Si cierras, se detendrá y lo ya copiado queda guardado (podrás reanudar).\n\n¿Cerrar de todos modos?",
                "IOS Photos", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _cts?.Cancel();
        }
        _poll.Stop();
        SaveSettings();
    }
}
