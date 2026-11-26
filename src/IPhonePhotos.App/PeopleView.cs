using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Organize;

namespace IPhonePhotos.App;

/// <summary>
/// Personas: las caras de las fotos agrupadas por parecido, con el nombre que les pongas. Todo en el PC.
/// Son datos biométricos: hay que activarlo (con aviso), se guardan fuera de la carpeta de fotos y se pueden borrar de un clic.
/// Se construye en código (sin XAML) para no depender de menús ni plantillas con eventos.
/// </summary>
public sealed class PeopleView : UserControl
{
    private readonly ActivityLog _log;
    private readonly StackPanel _people = new();
    private readonly WrapPanel _photos = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly Button _analyze = new() { Content = "Analizar caras", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _name = new() { Content = "Poner nombre…", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _confirm = new() { Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _delete = new() { Content = "Borrar todos los datos de Personas…", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
    private string _root = "";
    private AlbumData? _data;
    private List<PersonGroup> _groups = new();
    private PersonGroup? _selected;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource _thumbsCts = new();
    private bool _busy;
    private bool _dirty;

    public PeopleView(ActivityLog log)
    {
        _log = log;
        var left = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _people, Width = 260, Margin = new Thickness(0, 0, 16, 0) };
        var buttons = new WrapPanel { Children = { _analyze, _name, _confirm, _delete } };
        var right = new DockPanel();
        var head = new StackPanel { Children = { _title, _status, buttons } };
        DockPanel.SetDock(head, Dock.Top);
        right.Children.Add(head);
        right.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _photos });
        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(right);
        Content = root;
        _title.Text = "Personas";
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); // sigue al cambiar de modo claro/oscuro
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _analyze.Click += async (_, _) => await AnalyzeAsync();
        _name.Click += (_, _) => OnName();
        _confirm.Click += (_, _) => OnConfirm();
        _delete.Click += async (_, _) => await OnDeleteAsync();
        AutomationProperties.SetAutomationId(_analyze, "PeopleAnalyze");
        AutomationProperties.SetAutomationId(_delete, "PeopleDeleteAll");
        AutomationProperties.SetAutomationId(_confirm, "PeopleConfirmName");
        // Agrupar caras cuesta: solo se calcula al abrir la página, y fuera del hilo de la interfaz.
        IsVisibleChanged += async (_, _) => { if (IsVisible && _dirty) await ShowGroupsAsync(); };
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public void SetLibrary(string root, AlbumData data)
    {
        _root = root;
        _data = data;
        _selected = null;
        _dirty = true;
        if (IsVisible) _ = ShowGroupsAsync();
    }

    /// <summary>Carga lo ya analizado (sin tocar los modelos) en segundo plano y dibuja la lista.</summary>
    private async Task ShowGroupsAsync()
    {
        _dirty = false;
        _photos.Children.Clear();
        _people.Children.Clear();
        _name.Visibility = _confirm.Visibility = Visibility.Collapsed;
        _title.Text = "Personas";
        if (string.IsNullOrEmpty(_root)) return;
        if (!PeopleService.IsEnabled)
        {
            _status.Text = "Personas está desactivado. Al pulsar «Analizar caras» verás un aviso: son datos biométricos (también de otras personas) " +
                           "y se guardan solo en este PC, fuera de la carpeta de fotos.";
            _analyze.Content = "Analizar caras";
            return;
        }
        var root = _root;
        var total = _data?.Items.Count ?? 0;
        var (done, groups) = await Task.Run(() =>
        {
            var store = PeopleService.Load(root);
            // Sin nombre y con una sola cara, casi siempre es alguien de fondo: no se enseña para no llenar la lista de ruido.
            return (store.Done.Count, PeopleService.Group(root, store.Faces).Where(g => g.Name != null || g.Faces.Count >= 2).ToList());
        });
        if (root != _root) return; // mientras tanto se cambió de carpeta
        _groups = groups;
        _status.Text = done == 0
            ? "Todavía no se ha analizado ninguna foto. «Analizar caras» busca las caras en tu PC (nada se envía a internet); la primera vez tarda bastante, pero puedes parar y seguir otro día."
            : $"{done} de {total} fotos analizadas · {_groups.Count} personas.";
        _analyze.Content = done == 0 ? "Analizar caras" : "Analizar las fotos nuevas";
        foreach (var g in _groups) _people.Children.Add(Row(g));
        if (_groups.Count > 0) Select(_groups[0]);
    }

    private UIElement Row(PersonGroup g)
    {
        var img = new Image { Width = 48, Height = 48, Stretch = Stretch.UniformToFill, Clip = new EllipseGeometry(new Point(24, 24), 24, 24) };
        var crop = PeopleService.CropPath(_root, g.Best.Id);
        if (File.Exists(crop)) img.Source = ThumbLoader.Decode(crop, 96);
        var sub = g.Name == null && g.SuggestedName != null ? $"{g.PhotoCount} fotos · ¿{g.SuggestedName}?" : $"{g.PhotoCount} fotos";
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = g.Display, FontWeight = FontWeights.SemiBold },
            new TextBlock { Text = sub, FontSize = 12, Foreground = Brush("Muted") },
        } };
        // Un botón (y no un borde con clic) para que se pueda elegir con el teclado y lo lea el lector de pantalla.
        var row = new Button
        {
            Padding = new Thickness(8), Tag = g, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { img, text } }
        };
        AutomationProperties.SetName(row, $"{g.Display}, {sub}");
        row.Click += (_, _) => Select(g);
        return row;
    }

    private void Select(PersonGroup g)
    {
        _selected = g;
        foreach (Button r in _people.Children)
            r.Background = r.Tag == g ? Brush("NavSelected") : Brushes.Transparent;
        _title.Text = g.Display;
        _name.Visibility = Visibility.Visible;
        _name.Content = g.Name == null ? "Poner nombre…" : "Cambiar nombre…";
        _confirm.Visibility = g.Name == null && g.SuggestedName != null ? Visibility.Visible : Visibility.Collapsed;
        _confirm.Content = $"Sí, es {g.SuggestedName}";
        ShowPhotos(g);
    }

    private void ShowPhotos(PersonGroup g)
    {
        _thumbsCts.Cancel();
        _thumbsCts = new CancellationTokenSource();
        _photos.Children.Clear();
        if (_data == null) return;
        var keys = g.Faces.Select(f => f.PhotoKey).ToHashSet();
        var items = _data.Items.Where(i => keys.Contains(Album.Key(i.Media))).OrderByDescending(i => i.Date).Take(400).ToList();
        var photos = items.Select(i => new PreviewPhoto(Path.Combine(_root, i.Media.RelativePath), Path.GetFileName(i.Media.RelativePath),
            i.Date.ToString("d MMM yyyy HH:mm"))).ToList();
        for (var n = 0; n < items.Count; n++)
        {
            var item = items[n];
            var index = n;
            var thumb = new Thumb { Path = Path.Combine(_root, item.Media.RelativePath), Relative = item.Media.RelativePath, Item = item };
            var image = new Image { Width = 160, Height = 160, Stretch = Stretch.UniformToFill };
            var grid = new Grid { Children = { image } };
            var border = new Border { Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6), ClipToBounds = true, Cursor = Cursors.Hand,
                Background = Brush("ControlBrush"), Child = grid, ToolTip = item.Date.ToString("d MMM yyyy") + "\nDoble clic o Intro: verla en grande" };
            void Open() => Preview.Open(this, photos, index);
            grid.Children.Add(Preview.ExpandButton(border, photos[n].Name, Open));
            Preview.MakeOpenable(border, $"{photos[n].Name}, {photos[n].Info}", Open);
            _photos.Children.Add(border);
            _ = LoadAsync(thumb, image, _thumbsCts.Token);
        }
    }

    private static async Task LoadAsync(Thumb t, Image image, CancellationToken ct)
    {
        await ThumbLoader.LoadAsync(t, ct, 320);
        if (!ct.IsCancellationRequested) image.Source = t.Source;
    }

    private async void OnName()
    {
        if (_selected == null) return;
        var dialog = new PromptWindow("Nombre de la persona", _selected.Name ?? _selected.SuggestedName ?? "", "Guardar") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        await SaveNameAsync(dialog.Result?.Trim());
    }

    private async void OnConfirm()
    {
        if (_selected?.SuggestedName is { } suggested) await SaveNameAsync(suggested);
    }

    private async Task SaveNameAsync(string? name)
    {
        if (_selected == null || string.IsNullOrEmpty(name)) return;
        PeopleService.SetName(_root, _selected, name);
        await ShowGroupsAsync(); // si ya había otro grupo con ese nombre, se muestran juntos
        var again = _groups.FirstOrDefault(g => g.Name == name);
        if (again != null) Select(again);
    }

    private async Task OnDeleteAsync()
    {
        if (_busy || string.IsNullOrEmpty(_root)) return;
        if (MessageBox.Show(Window.GetWindow(this),
                "Se borrarán las caras, huellas, retratos y nombres de Personas de esta carpeta de fotos, y Personas quedará desactivado para todas tus carpetas (los datos de otras carpetas no se borran).\n\n" +
                "Las fotos no se tocan. No se puede deshacer. ¿Borrar?",
                "Borrar los datos de Personas", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            var root = _root;
            await Task.Run(() => PeopleService.DeleteAll(root));
            PeopleService.Disable();
            await ShowGroupsAsync();
            _status.Text = "Datos de Personas borrados y Personas desactivado.";
        }
        catch (Exception ex) { _status.Text = "No se pudieron borrar: " + ex.Message; _log.Error(ex.Message); }
    }

    private async Task AnalyzeAsync()
    {
        if (_busy) { _cts?.Cancel(); return; } // el mismo botón sirve para parar
        if (_data == null) return;
        if (!PeopleService.IsEnabled)
        {
            if (MessageBox.Show(Window.GetWindow(this), PeopleService.Notice + "\n\n¿Activar Personas?", "Activar Personas",
                    MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            PeopleService.Enable();
        }
        if (!await Task.Run(PeopleService.ModelsAvailable))
        {
            _status.Text = FaceModels.MissingMessage;
            return;
        }
        _busy = true;
        _cts = new CancellationTokenSource();
        _analyze.Content = "Parar";
        var progress = new Progress<(int Done, int Total)>(p => _status.Text = $"Analizando caras: {p.Done} de {p.Total} fotos…");
        var items = _data.Items;
        var root = _root;
        string? outcome = null;
        try
        {
            await Task.Run(() => PeopleService.Index(root, items, progress, _cts.Token));
        }
        catch (OperationCanceledException) { outcome = "Análisis parado: lo hecho queda guardado."; }
        catch (Exception ex) { outcome = "No se pudo analizar: " + ex.Message; }
        finally { _busy = false; }
        await ShowGroupsAsync();
        if (outcome != null) _status.Text = outcome;
        if (FaceModels.Notice != null) { _status.Text += " " + FaceModels.Notice; _log.Warn(FaceModels.Notice); }
    }
}
