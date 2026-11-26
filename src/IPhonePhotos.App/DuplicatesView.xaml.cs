using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Organize;
using IPhonePhotos.Core.Transfer;

namespace IPhonePhotos.App;

/// <summary>Una foto de un grupo de repetidas, con su miniatura y si se conserva o se aparta.</summary>
public sealed class DupCard : INotifyPropertyChanged
{
    private bool _keep;
    public required DupPhoto Photo { get; init; }
    public required Thumb Thumb { get; init; }
    public bool Recommended { get; init; }
    public string Name => Path.GetFileName(Photo.Item.Media.RelativePath);
    public string ExpandName => "Ampliar foto " + Name;
    public string Info => $"{Photo.Megapixels:0.#} MP · {Format.Bytes(Photo.Size)} · {Photo.Item.Date:d MMM yyyy HH:mm}";
    public string ScoreText => $"Calidad {Photo.Score}/100" + (Photo.Notes.Length > 0 ? $" · {Photo.Notes}" : "");
    public string Label => _keep ? (Recommended ? "Me quedo con esta (recomendada)" : "Me quedo con esta") : (Recommended ? "Era la recomendada · clic para quedarme con esta" : "Se aparta · clic para quedarme con esta");
    public Brush Outline => _keep ? new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F)) : Brushes.Transparent;
    public Brush LabelBackground => _keep ? new SolidColorBrush(Color.FromRgb(0xDF, 0xF6, 0xDD)) : new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    public Brush LabelForeground => _keep ? new SolidColorBrush(Color.FromRgb(0x0B, 0x5A, 0x0B)) : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
    public bool Keep
    {
        get => _keep;
        set
        {
            _keep = value;
            foreach (var n in new[] { nameof(Keep), nameof(Label), nameof(Outline), nameof(LabelBackground), nameof(LabelForeground) })
                PropertyChanged?.Invoke(this, new(n));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Revisión de fotos repetidas o muy parecidas: por cada grupo, la app recomienda una y tú eliges con cuál te quedas.
/// Las demás se APARTAN a «_Duplicados» (no se borran) y se puede deshacer moviéndolas de vuelta.
/// </summary>
public partial class DuplicatesView : UserControl
{
    private readonly ActivityLog _log;
    private string _folder = "";
    private AlbumData? _data;
    private List<DupGroup> _groups = new();
    private readonly Dictionary<DupGroup, List<DupCard>> _cards = new();
    private CancellationTokenSource _thumbCts = new();
    private CancellationTokenSource? _searchCts;
    private bool _busy;

    /// <summary>Se avisa cuando se han apartado fotos, para que el álbum se vuelva a cargar.</summary>
    public event Action? FilesMoved;

    public DuplicatesView(ActivityLog log)
    {
        InitializeComponent();
        _log = log;
    }

    public void SetLibrary(string folder, AlbumData? data)
    {
        _folder = folder;
        _data = data;
    }

    private int Distance => int.TryParse((Sensitivity.SelectedItem as ComboBoxItem)?.Tag as string, out var d) ? d : DuplicateService.DefaultDistance;

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (_searchCts != null) { _searchCts.Cancel(); return; } // el mismo botón sirve para parar
        if (_busy) return;
        if (!Directory.Exists(_folder)) { StatusText.Text = "La carpeta de fotos todavía no existe."; return; }
        SetBusy(true);
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        SearchButton.Content = "Parar";
        SearchButton.IsEnabled = true;
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        StatusText.Text = "Comparando fotos… (la primera vez tarda varios minutos; las siguientes, segundos)";
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                Bar.IsIndeterminate = false;
                Bar.Value = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
                DetailText.Text = $"Analizando fotos: {p.Done} de {p.Total}";
            });
            var distance = Distance;
            var folder = _folder;
            int edited;
            (_groups, edited) = await Task.Run(() =>
            {
                var items = (_data ?? Album.Load(folder)).Items;
                var groups = DuplicateService.Find(folder, items, distance, progress, token);
                return (groups, EditedVersions.Find(MediaScanner.Scan(folder, token, includeAlbums: true)).Count);
            }, token);
            ShowEdited(edited);
            _cards.Clear();
            ShowGroups();
            StatusText.Text = _groups.Count == 0
                ? "No se han encontrado fotos repetidas."
                : $"{_groups.Count} grupos de fotos repetidas o parecidas. Revisa cada grupo: la marcada en verde es la que te quedas.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Búsqueda parada. Lo ya analizado se aprovecha la próxima vez.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally
        {
            _searchCts.Dispose();
            _searchCts = null;
            SearchButton.Content = "Buscar repetidas";
            DetailText.Text = "";
            Bar.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    // ------------------------------------------------------------------ Versiones editadas (IMG_E… / .AAE)

    private void ShowEdited(int count)
    {
        EditedPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditedText.Text = $"{count} fotos tienen versión editada (IMG_E…). No son repetidas: son la misma foto antes y después de editarla.";
    }

    private async void OnSetAsideEdited(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var folder = _folder;
        var plan = await Task.Run(() => EditedVersions.PlanSetAsideOriginals(folder));
        if (plan.Items.Count == 0) { ShowEdited(0); return; }
        // Simulación antes de mover: qué y cuánto, y que no se borra nada.
        var aae = plan.Items.Count(i => i.From.EndsWith(".aae", StringComparison.OrdinalIgnoreCase));
        if (MessageBox.Show(Window.GetWindow(this),
                $"Simulación: se moverían {plan.Items.Count - aae} fotos originales y {aae} archivos .AAE ({Format.Bytes(plan.TotalBytes)}) " +
                $"a «{EditedVersions.Folder}». La versión editada se queda donde está.\n\nNo se borra nada (tampoco en el iPhone): " +
                "puedes devolverlos desde esa carpeta. ¿Apartar ahora?",
                "Apartar las originales de fotos editadas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        using var gate = LibraryGate.TryEnter("apartar originales editadas");
        if (gate == null) { StatusText.Text = LibraryGate.BusyMessage; return; }
        SetBusy(true);
        try
        {
            var result = await Task.Run(() =>
            {
                using var store = FileMover.OpenExistingManifest(folder);
                return new EditedVersions(_log).Apply(folder, store, plan, null, CancellationToken.None);
            });
            StatusText.Text = $"Apartados {result.Moved} archivos a «{EditedVersions.Folder}»." +
                              (result.Failed.Count > 0 ? $" {result.Failed.Count} con error." : "") +
                              (result.ReportPath != null ? $" Informe: {result.ReportPath}" : "");
            ShowEdited(0);
            FilesMoved?.Invoke();
        }
        catch (Exception ex)
        {
            var u = IPhonePhotos.Core.Connection.ErrorExplainer.Explain(ex);
            StatusText.Text = $"{u.Cause} {u.Action}";
            _log.Error(ex.Message);
        }
        finally { SetBusy(false); }
    }

    private void ShowGroups()
    {
        GroupList.ItemsSource = _groups.Select((g, i) =>
            $"Grupo {i + 1}  ·  {g.Photos.Count} fotos  ·  {Format.Bytes(g.Photos.Where((_, k) => k != g.Recommended).Sum(p => p.Size))}").ToList();
        if (_groups.Count > 0) GroupList.SelectedIndex = 0; else Cards.ItemsSource = null;
        UpdateButtons();
    }

    private List<DupCard> CardsOf(DupGroup g)
    {
        if (_cards.TryGetValue(g, out var existing)) return existing;
        var list = g.Photos.Select((p, i) => new DupCard
        {
            Photo = p,
            Recommended = i == g.Recommended,
            Keep = i == g.Recommended,
            Thumb = new Thumb { Path = Path.Combine(_folder, p.Item.Media.RelativePath) }
        }).ToList();
        _cards[g] = list;
        return list;
    }

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        _thumbCts.Cancel();
        _thumbCts = new CancellationTokenSource();
        var i = GroupList.SelectedIndex;
        if (i < 0 || i >= _groups.Count) { Cards.ItemsSource = null; UpdateButtons(); return; }
        var cards = CardsOf(_groups[i]);
        Cards.ItemsSource = cards;
        foreach (var c in cards.Where(c => c.Thumb.Source == null)) _ = ThumbLoader.LoadAsync(c.Thumb, _thumbCts.Token, 500);
        UpdateButtons();
    }

    // Clic en la etiqueta de la foto: es la que se conserva (solo una por grupo).
    private void OnPick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true; // que no abra también la vista grande
        if (((FrameworkElement)sender).DataContext is DupCard card) Pick(card);
    }

    private void Pick(DupCard card)
    {
        if (Cards.ItemsSource is not List<DupCard> all || !all.Contains(card)) return;
        foreach (var c in all) c.Keep = c == card;
        UpdateButtons();
    }

    // Clic en el resto de la foto: se abre en grande, con flechas para pasar a las otras del grupo.
    private void OnCardOpen(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Preview.FromButton(e.OriginalSource)) return;
        OpenCard(sender);
    }

    private void OnCardExpand(object sender, RoutedEventArgs e) { e.Handled = true; OpenCard(sender); }

    private void OnCardKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space && e.OriginalSource == sender) { e.Handled = true; OpenCard(sender); }
    }

    private void OpenCard(object sender)
    {
        if (((FrameworkElement)sender).DataContext is not DupCard card || Cards.ItemsSource is not List<DupCard> all) return;
        var photos = all.Select(c => new PreviewPhoto(c.Thumb.Path, c.Name, c.Info, c.ScoreText + (c.Recommended ? " · recomendada por la app" : ""))).ToList();
        Preview.Open(this, photos, all.IndexOf(card), i => Pick(all[i]), i => all[i].Keep);
    }

    private void SetBusy(bool busy) { _busy = busy; SearchButton.IsEnabled = !busy; UpdateButtons(); }

    private void UpdateButtons()
    {
        ApplyButton.IsEnabled = !_busy && GroupList.SelectedIndex >= 0 && _groups.Count > 0;
        ApplyAllButton.IsEnabled = !_busy && _groups.Count > 0;
    }

    private IEnumerable<AlbumItem> ToSetAside(DupGroup g)
        => CardsOf(g).Where(c => !c.Keep).Select(c => c.Photo.Item);

    private async void OnApplyGroup(object sender, RoutedEventArgs e)
    {
        var i = GroupList.SelectedIndex;
        if (i < 0 || i >= _groups.Count) return;
        await ApplyAsync(new List<DupGroup> { _groups[i] }, confirm: false);
    }

    private async void OnApplyAll(object sender, RoutedEventArgs e) => await ApplyAsync(_groups.ToList(), confirm: true);

    private async Task ApplyAsync(List<DupGroup> groups, bool confirm)
    {
        var items = groups.SelectMany(ToSetAside).ToList();
        if (items.Count == 0) return;
        var bytes = items.Sum(it => it.Media.Size);
        if (confirm && MessageBox.Show(Window.GetWindow(this),
                $"Se van a apartar {items.Count} fotos ({Format.Bytes(bytes)}) de {groups.Count} grupos a «{DuplicateService.QuarantineFolder}», " +
                "conservando en cada grupo la que está marcada.\n\nNo se borra nada: puedes recuperarlas de esa carpeta. ¿Continuar?",
                "Apartar fotos repetidas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        using var gate = LibraryGate.TryEnter("apartar repetidas");
        if (gate == null) { StatusText.Text = LibraryGate.BusyMessage; return; }
        SetBusy(true);
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = false;
        Bar.Value = 0;
        try
        {
            var progress = new Progress<TransferProgress>(p =>
            {
                Bar.Value = p.FilesTotal > 0 ? p.FilesDone * 100.0 / p.FilesTotal : 0;
                DetailText.Text = $"Apartando {p.FilesDone} de {p.FilesTotal}";
            });
            var folder = _folder;
            var result = await Task.Run(() =>
            {
                using var store = FileMover.OpenExistingManifest(folder);
                return new DuplicateService(_log).Quarantine(folder, store, items, progress, CancellationToken.None);
            });
            foreach (var g in groups) { _cards.Remove(g); _groups.Remove(g); }
            ShowGroups();
            StatusText.Text = $"Apartadas {result.Moved} fotos ({Format.Bytes(result.BytesMoved)}) a «{DuplicateService.QuarantineFolder}»." +
                              (result.Failed.Count > 0 ? $" {result.Failed.Count} con error." : "") +
                              (result.ReportPath != null ? $" Informe: {result.ReportPath}" : "");
            FilesMoved?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally
        {
            DetailText.Text = "";
            Bar.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }
}
