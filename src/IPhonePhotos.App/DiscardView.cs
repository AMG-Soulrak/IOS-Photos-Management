using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Organize;
using IPhonePhotos.Core.Transfer;

namespace IPhonePhotos.App;

/// <summary>
/// Capturas de pantalla y fotos «fuera de contexto» (tickets, papeles). La app propone y tú marcas: las marcadas se APARTAN a
/// «_Descartadas» (no se borran; para eliminarlas del todo, vacía esa carpeta tú). Se construye en código, como PeopleView.
/// </summary>
public sealed class DiscardView : UserControl
{
    private sealed class Card
    {
        public required DiscardCandidate Candidate { get; init; }
        public required CheckBox Check { get; init; }
        public required Border Frame { get; init; }
    }

    private readonly ActivityLog _log;
    private readonly WrapPanel _grid = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { Foreground = (Brush)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly Button _search = new() { Content = "Buscar", Padding = new Thickness(14, 6, 14, 6) };
    private readonly Button _apply = new() { Content = "Apartar las marcadas", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly Button _folderButton = new() { Content = "Abrir «_Descartadas»", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _filter = new() { Width = 220, Margin = new Thickness(16, 0, 0, 0), ItemsSource = new[] { "Todo", "Solo capturas de pantalla", "Solo posibles documentos" }, SelectedIndex = 0 };
    private readonly ProgressBar _bar = new() { Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
    private readonly List<Card> _cards = new();
    private CancellationTokenSource _thumbsCts = new();
    private CancellationTokenSource? _searchCts;
    private string _root = "";
    private AlbumData? _data;
    private bool _busy;

    public event Action? FilesMoved;

    public DiscardView(ActivityLog log)
    {
        _log = log;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); // sigue al cambiar de modo claro/oscuro
        var title = new TextBlock { Text = "Descartar capturas y fotos fuera de contexto", FontSize = 20, FontWeight = FontWeights.SemiBold };
        var info = new TextBlock
        {
            Text = "Busca capturas de pantalla (seguro) y fotos que parecen un papel o ticket (aproximado: revisa antes de apartar). " +
                   "Las que marques se apartan a «_Descartadas»; no se borra nada.",
            Foreground = (Brush)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _search, _apply, _folderButton, _filter } };
        var head = new StackPanel { Children = { title, info, buttons, _bar, _status } };
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grid });
        Content = root;
        _search.Click += async (_, _) => await SearchAsync();
        _apply.Click += async (_, _) => await ApplyAsync();
        _folderButton.Click += (_, _) => OpenFolder();
        _filter.SelectionChanged += (_, _) => Render();
    }

    public void SetLibrary(string root, AlbumData data)
    {
        _root = root;
        _data = data;
    }

    private async Task SearchAsync()
    {
        if (_searchCts != null) { _searchCts.Cancel(); return; } // el mismo botón sirve para parar
        if (_busy || _data == null) return;
        SetBusy(true);
        _searchCts = new CancellationTokenSource();
        _search.Content = "Parar";
        _search.IsEnabled = true;
        _bar.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = true;
        _status.Text = "Buscando… (la primera vez mira cada foto; después es inmediato)";
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                _bar.IsIndeterminate = false;
                _bar.Value = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
            });
            var items = _data.Items;
            var root = _root;
            var token = _searchCts.Token;
            var found = await Task.Run(() => DiscardService.Find(root, items, progress, token), token);
            _cards.Clear();
            foreach (var c in found) _cards.Add(MakeCard(c));
            Render();
        }
        catch (OperationCanceledException) { _status.Text = "Búsqueda parada."; }
        catch (Exception ex) { _status.Text = "Error: " + ex.Message; _log.Error(ex.Message); }
        finally
        {
            _searchCts.Dispose();
            _searchCts = null;
            _search.Content = "Buscar";
            _bar.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private Card MakeCard(DiscardCandidate c)
    {
        // Las capturas vienen marcadas (son seguras); los posibles documentos, sin marcar hasta que los revises.
        var check = new CheckBox { IsChecked = c.Kind == DiscardKind.Screenshot, Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        check.Click += (_, _) => UpdateApply();
        var image = new Image { Width = 170, Height = 170, Stretch = Stretch.UniformToFill };
        var label = new TextBlock
        {
            Text = c.Kind == DiscardKind.Screenshot ? "Captura" : "¿Documento?", FontSize = 11, Margin = new Thickness(6, 2, 6, 4),
            Foreground = (Brush)Application.Current.FindResource("Muted")
        };
        var frame = new Border
        {
            Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6), ClipToBounds = true, Cursor = Cursors.Hand,
            Background = (Brush)Application.Current.FindResource("ControlBrush"), ToolTip = $"{c.Reason}\n{c.Item.Media.RelativePath}",
            Child = new StackPanel { Children = { new Grid { Children = { image, check } }, label } }
        };
        var path = Path.Combine(_root, c.Item.Media.RelativePath);
        image.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); };
        var thumb = new Thumb { Path = path };
        _ = Load(thumb, image);
        return new Card { Candidate = c, Check = check, Frame = frame };
    }

    private async Task Load(Thumb t, Image image)
    {
        await ThumbLoader.LoadAsync(t, _thumbsCts.Token, 340);
        image.Source = t.Source;
    }

    private bool Visible(Card c) => _filter.SelectedIndex switch
    {
        1 => c.Candidate.Kind == DiscardKind.Screenshot,
        2 => c.Candidate.Kind == DiscardKind.Document,
        _ => true
    };

    private void Render()
    {
        _grid.Children.Clear();
        foreach (var c in _cards.Where(Visible)) _grid.Children.Add(c.Frame);
        var shots = _cards.Count(c => c.Candidate.Kind == DiscardKind.Screenshot);
        _status.Text = _cards.Count == 0
            ? "No se ha encontrado nada que descartar (o aún no has pulsado Buscar)."
            : $"{shots} capturas de pantalla y {_cards.Count - shots} posibles documentos. Doble clic abre la foto; marca o desmarca con la casilla.";
        UpdateApply();
    }

    private IEnumerable<Card> Marked => _cards.Where(c => Visible(c) && c.Check.IsChecked == true);

    private void UpdateApply()
    {
        var n = Marked.Count();
        _apply.IsEnabled = !_busy && n > 0;
        _apply.Content = n > 0 ? $"Apartar las marcadas ({n})" : "Apartar las marcadas";
    }

    private void SetBusy(bool busy) { _busy = busy; _search.IsEnabled = !busy; UpdateApply(); }

    private async Task ApplyAsync()
    {
        var marked = Marked.ToList();
        if (marked.Count == 0) return;
        var items = marked.Select(c => c.Candidate.Item).ToList();
        if (MessageBox.Show(Window.GetWindow(this),
                $"Se van a apartar {items.Count} fotos ({Format.Bytes(items.Sum(i => i.Media.Size))}) a «{DiscardService.Folder}».\n\n" +
                "No se borra nada: puedes recuperarlas de esa carpeta, o vaciarla tú cuando estés seguro. ¿Continuar?",
                "Apartar fotos descartadas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        using var gate = LibraryGate.TryEnter("apartar descartes");
        if (gate == null) { _status.Text = LibraryGate.BusyMessage; return; }
        SetBusy(true);
        try
        {
            var root = _root;
            var result = await Task.Run(() =>
            {
                using var store = FileMover.OpenExistingManifest(root);
                return new DiscardService(_log).SetAside(root, store, items, null, CancellationToken.None);
            });
            foreach (var c in marked) _cards.Remove(c);
            Render();
            _status.Text = $"Apartadas {result.Moved} fotos ({Format.Bytes(result.BytesMoved)}) a «{DiscardService.Folder}»." +
                           (result.Failed.Count > 0 ? $" {result.Failed.Count} con error." : "");
            FilesMoved?.Invoke();
        }
        catch (Exception ex) { _status.Text = "Error: " + ex.Message; _log.Error(ex.Message); }
        finally { SetBusy(false); }
    }

    private void OpenFolder()
    {
        var dir = Path.Combine(_root, DiscardService.Folder);
        if (!Directory.Exists(dir)) { _status.Text = "Todavía no hay nada apartado."; return; }
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
