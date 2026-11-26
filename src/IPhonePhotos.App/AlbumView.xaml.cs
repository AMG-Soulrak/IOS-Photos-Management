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

/// <summary>Un álbum propio (carpeta dentro de «Álbumes») con su número de fotos.</summary>
public sealed record AlbumEntry(string Name, int Count, bool IsInbox = false)
{
    public string Display => $"{Name}  ·  {Count}";
    /// <summary>Carpeta del álbum dentro de la biblioteca (dentro de «Álbumes»), o «Nuevos» para la bandeja de entrada.</summary>
    public string RelativeFolder => IsInbox ? AlbumService.InboxFolder : System.IO.Path.Combine(AlbumService.Folder, Name);
}

/// <summary>
/// Álbum de la carpeta de fotos: miniaturas agrupadas por fecha o por lugar (GPS), y «Mis álbumes»: carpetas propias a las que se
/// arrastran fotos (se mueven a esa carpeta). Los Live Photos llevan la etiqueta LIVE y se abren en su visor.
/// shortcut: se muestran 200 fotos por grupo y «Mostrar más» añade otras 200; los cuadros se crean todos (WPF no trae un panel
/// envolvente virtualizado), pero solo los cercanos a la vista guardan su imagen (<see cref="TrimThumbs"/>).
/// </summary>
public partial class AlbumView : UserControl
{
    private const int PageSize = 200;
    private const string DragFormat = "iphotos-thumbs";
    private readonly ActivityLog _log;
    private string _folder = "";
    private AlbumData? _data;
    private List<AlbumGroup> _groups = new();
    private bool _byPlace, _syncing, _busy;
    private AlbumGroup? _group;
    private string? _currentAlbum;
    private string? _restoreGroup, _restoreAlbum;
    private int _shown;
    private CancellationTokenSource _thumbCts = new();
    private CancellationTokenSource? _loadCts;
    private Point _dragOrigin;
    private Thumb? _downThumb;
    private bool _maybeCollapse;

    public AlbumView(ActivityLog log)
    {
        InitializeComponent();
        AlbumFilter.SelectionChanged += (_, _) => { if (!_syncing) ShowGroups(); };
        _log = log;
        // Los álbumes propios viven en la barra lateral de la ventana principal, como submenú de «Álbum».
        if (AlbumsBox.Parent is Panel host) host.Children.Remove(AlbumsBox);
        Focusable = true; // para recibir Ctrl + A
        PreviewMouseDown += (_, _) => { if (!AlbumFilter.IsDropDownOpen) Focus(); }; // con el desplegable abierto, Focus() lo cerraba antes de elegir
    }

    /// <summary>Lista de álbumes propios (con «+ Nuevo álbum»), para colocarla en la barra lateral.</summary>
    public UIElement AlbumsPanel => AlbumsBox;

    /// <summary>Se avisa cuando se elige un álbum, para que la ventana principal muestre la página del álbum.</summary>
    public event Action? AlbumPicked;

    /// <summary>Datos ya cargados, para que otras páginas no tengan que volver a leer la carpeta.</summary>
    public AlbumData? Data => _data;

    private System.Collections.ObjectModel.ObservableCollection<Thumb> Shown =>
        Thumbs.ItemsSource as System.Collections.ObjectModel.ObservableCollection<Thumb> ?? new();

    // La selección se guarda por ruta (no en las miniaturas) para poder marcar todo el grupo aunque solo se vea una parte.
    private readonly HashSet<string> _sel = new(StringComparer.OrdinalIgnoreCase);
    private Thumb? _anchor;
    private List<AlbumItem> SelectedItems => _group?.Items.Where(i => _sel.Contains(i.Media.RelativePath)).ToList() ?? new();

    private static readonly Brush FolderBack = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Brush AlbumBack = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x00, 0x5F, 0xB8)));
    private static readonly Brush InboxBack = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xB4, 0x5F, 0x00)));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Dónde está la foto: «Álbum · nombre», «Nuevos» o su carpeta.</summary>
    private static (string Text, Brush Back) WhereOf(string relativePath)
    {
        var parts = relativePath.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (parts.Length > 2 && parts[0].Equals(AlbumService.Folder, StringComparison.OrdinalIgnoreCase)) return ("Álbum · " + parts[1], AlbumBack);
        if (parts.Length > 1 && parts[0].Equals(AlbumService.InboxFolder, StringComparison.OrdinalIgnoreCase)) return ("Nuevos", InboxBack);
        return (parts.Length > 1 ? string.Join(" \\ ", parts[..^1]) : "(raíz)", FolderBack);
    }

    private static bool IsInAlbum(AlbumItem i) => WhereOf(i.Media.RelativePath).Text.StartsWith("Álbum · ");

    private void SetSelected(Thumb t, bool on)
    {
        t.Selected = on;
        if (t.Item == null) return;
        if (on) _sel.Add(t.Item.Media.RelativePath); else _sel.Remove(t.Item.Media.RelativePath);
    }

    private void ShowSelection()
    {
        if (_sel.Count > 0) StatusText.Text = $"{N(_sel.Count, "foto seleccionada", "fotos seleccionadas")}: arrástralas a un álbum (Mayús + clic marca un rango, Ctrl + A todas).";
    }

    private static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    /// <summary>Se avisa cada vez que se vuelven a leer las fotos (para que las demás páginas no trabajen con datos viejos).</summary>
    public event Action? DataLoaded;

    private void OnDiscardSelected(object sender, RoutedEventArgs e) => _ = DiscardSelectedAsync();

    // Quita de aquí las fotos seleccionadas: se apartan a «_Descartadas» (no se borran, se pueden recuperar de esa carpeta).
    private async Task DiscardSelectedAsync()
    {
        var items = SelectedItems;
        if (items.Count == 0) { StatusText.Text = "Selecciona primero las fotos que quieras descartar."; return; }
        if (_busy) return;
        if (MessageBox.Show(Window.GetWindow(this),
                $"Se van a apartar {N(items.Count, "foto", "fotos")} ({Format.Bytes(items.Sum(i => i.Media.Size))}) a «{DiscardService.Folder}» y dejarán de salir en el álbum.\n\n" +
                "No se borra nada: puedes recuperarlas de esa carpeta, o vaciarla tú cuando estés seguro. ¿Continuar?",
                "Descartar fotos", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        using var gate = LibraryGate.TryEnter("descartar fotos");
        if (gate == null) { StatusText.Text = LibraryGate.BusyMessage; return; }
        _busy = true;
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        StatusText.Text = "Apartando fotos…";
        _restoreAlbum = _currentAlbum;
        _restoreGroup = _currentAlbum == null ? _group?.Name : null;
        try
        {
            var folder = _folder;
            var result = await Task.Run(() =>
            {
                using var store = FileMover.OpenExistingManifest(folder);
                return new DiscardService(_log).SetAside(folder, store, items, null, CancellationToken.None);
            });
            await LoadAsync(_folder);
            StatusText.Text = $"{N(items.Count, "foto apartada", "fotos apartadas")} a «{DiscardService.Folder}»." + (result.Failed.Count > 0 ? $" {result.Failed.Count} con error (mira el registro)." : "");
        }
        catch (Exception ex)
        {
            StatusText.Text = "No se pudieron apartar: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally { _busy = false; Bar.Visibility = Visibility.Collapsed; }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SelectAll();
    private void OnSelectNone(object sender, RoutedEventArgs e) => ClearSelection();

    private void SelectAll()
    {
        if (_group == null) return;
        foreach (var i in _group.Items) _sel.Add(i.Media.RelativePath);
        foreach (var t in Shown) t.Selected = true;
        ShowSelection();
    }

    private void ClearSelection()
    {
        _sel.Clear();
        foreach (var t in Shown) t.Selected = false;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; }
        else if (e.Key == Key.Delete && _sel.Count > 0) { _ = DiscardSelectedAsync(); e.Handled = true; }
    }

    public async Task LoadAsync(string folder)
    {
        _folder = folder;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        if (!Directory.Exists(folder)) { StatusText.Text = "La carpeta de fotos todavía no existe. Descarga las del iPhone o elige otra."; _data = null; ShowGroups(); return; }
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        StatusText.Text = "Leyendo las fotos…";
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                Bar.IsIndeterminate = false;
                Bar.Value = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
                StatusText.Text = $"Leyendo metadatos: {p.Done} de {p.Total} (la primera vez tarda; la siguiente es inmediata)";
            });
            _data = await Task.Run(() => Album.Load(folder, progress, token), token);
            ShowGroups();
            DataLoaded?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally { Bar.Visibility = Visibility.Collapsed; }
    }

    private void ShowGroups()
    {
        if (_data == null) { GroupList.ItemsSource = null; AlbumList.ItemsSource = null; Thumbs.ItemsSource = null; MoreButton.Visibility = Visibility.Collapsed; return; }
        PlacesBanner.Visibility = _byPlace && !_data.HasPlaceNames ? Visibility.Visible : Visibility.Collapsed;
        // Filtro: todas / solo las que están en un álbum / solo las que no están en ninguno.
        var shown = AlbumFilter.SelectedIndex switch
        {
            1 => _data.Items.Where(IsInAlbum).ToList(),
            2 => _data.Items.Where(i => !IsInAlbum(i)).ToList(),
            _ => _data.Items
        };
        _groups = _byPlace ? Album.ByPlace(shown) : Album.ByDate(shown);
        GroupList.ItemsSource = _groups.Select(g => $"{g.Name}  ·  {g.Items.Count}").ToList();
        RefreshAlbums();
        var noGps = shown.Count(i => i.Lat == null);
        StatusText.Text = $"{N(shown.Count, "foto", "fotos")} en {N(_groups.Count, "grupo", "grupos")}" +
                          (noGps > 0 ? $"; {noGps} sin GPS" : "") +
                          (_data.VideosAndOthers > 0 ? $". {_data.VideosAndOthers} vídeos y otros archivos no se muestran." : ".");

        // Tras mover fotos se vuelve a donde se estaba (el mismo álbum o el mismo grupo).
        var album = _restoreAlbum;
        var wanted = _restoreGroup;
        _restoreAlbum = _restoreGroup = null;
        // Se vuelve a mostrar el grupo a la fuerza: si el índice seleccionado no cambia, la lista no avisa y las miniaturas
        // se quedarían con los datos viejos (etiquetas y selección de antes de mover).
        var albumIndex = album == null ? -1 : AlbumList.Items.OfType<AlbumEntry>().ToList().FindIndex(a => a.Name == album);
        if (albumIndex >= 0)
        {
            var a = (AlbumEntry)AlbumList.Items[albumIndex];
            _syncing = true;
            GroupList.SelectedIndex = -1;
            AlbumList.SelectedIndex = albumIndex;
            _syncing = false;
            _currentAlbum = a.Name;
            Show(new AlbumGroup(a.Name, Album.InFolder(_data.Items, a.RelativeFolder)));
        }
        else if (_groups.Count > 0)
        {
            var index = Math.Max(0, wanted == null ? 0 : _groups.FindIndex(g => g.Name == wanted));
            _syncing = true;
            AlbumList.SelectedIndex = -1;
            GroupList.SelectedIndex = index;
            _syncing = false;
            _currentAlbum = null;
            Show(_groups[index]);
        }
        else { Thumbs.ItemsSource = null; MoreButton.Visibility = Visibility.Collapsed; }
    }

    private void RefreshAlbums()
    {
        var items = _data?.Items ?? new List<AlbumItem>();
        var entries = AlbumService.List(_folder).Select(n => new AlbumEntry(n, Album.InAlbum(items, n).Count)).ToList();
        // «Nuevos»: donde caen las fotos recién descargadas del iPhone. Se reparten en álbumes arrastrándolas desde aquí.
        var inbox = Album.InFolder(items, AlbumService.InboxFolder).Count;
        if (inbox > 0) entries.Insert(0, new AlbumEntry(AlbumService.InboxFolder, inbox, IsInbox: true));
        _syncing = true;
        AlbumList.ItemsSource = entries;
        _syncing = false;
        AlbumHint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || GroupList.SelectedIndex < 0 || GroupList.SelectedIndex >= _groups.Count) return;
        _syncing = true;
        AlbumList.SelectedIndex = -1;
        _syncing = false;
        _currentAlbum = null;
        Show(_groups[GroupList.SelectedIndex]);
    }

    private void OnAlbumSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || AlbumList.SelectedItem is not AlbumEntry a) return;
        _syncing = true;
        GroupList.SelectedIndex = -1;
        _syncing = false;
        _currentAlbum = a.Name;
        AlbumPicked?.Invoke();
        Show(new AlbumGroup(a.Name, Album.InFolder(_data?.Items ?? new List<AlbumItem>(), a.RelativeFolder)));
    }

    private void Show(AlbumGroup group)
    {
        _thumbCts.Cancel();
        _thumbCts = new CancellationTokenSource();
        _group = group;
        _sel.Clear();
        _anchor = null;
        _shown = 0;
        Thumbs.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<Thumb>();
        Scroller.ScrollToTop();
        ShowMore();
    }

    private void OnMore(object sender, RoutedEventArgs e) => ShowMore();

    private void ShowMore()
    {
        if (_group == null) { MoreButton.Visibility = Visibility.Collapsed; return; }
        var page = _group.Items.Skip(_shown).Take(PageSize).Select(i => new Thumb
        {
            Path = System.IO.Path.Combine(_folder, i.Media.RelativePath),
            Relative = i.Media.RelativePath,
            LiveClip = i.LiveClip,
            Item = i,
            Where = WhereOf(i.Media.RelativePath).Text,
            WhereBack = WhereOf(i.Media.RelativePath).Back,
            Selected = _sel.Contains(i.Media.RelativePath),
            Tip = $"{System.IO.Path.GetFileName(i.Media.RelativePath)}\n{i.Date:d MMM yyyy HH:mm}" + (i.Place != null ? $"\n{i.Place}" : "")
                  + (i.LiveClip != null ? "\nLive Photo: clic derecho para ver el clip y elegir fotograma" : "")
                  + $"\nEstá en: {WhereOf(i.Media.RelativePath).Text}"
                  + "\nDoble clic o Intro: verla en grande. Arrástrala a un álbum (Ctrl o Mayús + clic para elegir varias)"
        }).ToList();
        _shown += page.Count;
        // Se añaden a la colección (antes se cambiaba la lista entera y WPF volvía a crear todos los cuadros).
        var shown = Shown;
        foreach (var t in page) shown.Add(t);
        MoreButton.Content = $"Mostrar más ({_group.Items.Count - _shown} restantes)";
        MoreButton.Visibility = _shown < _group.Items.Count ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(TrimThumbs, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // shortcut: cada cuadro mide 150 px + 3 de margen por lado (plantilla de Thumbs); si cambia la plantilla, cambiar Cell.
    private const double Cell = 156;
    private const int SpareRows = 3;

    private void OnThumbsScroll(object sender, ScrollChangedEventArgs e) => TrimThumbs();

    /// <summary>
    /// Solo las miniaturas visibles (y 3 filas por arriba y por abajo) tienen imagen: las demás se sueltan y se vuelven a
    /// decodificar al acercarse. Así «Mostrar más» no acumula memoria con cada página.
    /// </summary>
    private void TrimThumbs()
    {
        var list = Shown;
        if (list.Count == 0 || Thumbs.ActualWidth <= 0) return;
        var perRow = Math.Max(1, (int)(Thumbs.ActualWidth / Cell));
        var firstRow = (int)(Scroller.VerticalOffset / Cell) - SpareRows;
        var lastRow = (int)((Scroller.VerticalOffset + Scroller.ViewportHeight) / Cell) + SpareRows;
        int from = Math.Max(0, firstRow * perRow), to = Math.Min(list.Count - 1, (lastRow + 1) * perRow - 1);
        var token = _thumbCts.Token;
        for (var i = 0; i < list.Count; i++)
        {
            var t = list[i];
            if (i < from || i > to) { if (t.Requested || t.Source != null) { t.Requested = false; t.Source = null; } }
            else if (!t.Requested) { t.Requested = true; _ = ThumbLoader.LoadAsync(t, token); }
        }
    }

    // ------------------------------------------------------------------ Selección, abrir y arrastrar fotos

    private static Thumb? ThumbOf(object sender) => (sender as FrameworkElement)?.DataContext as Thumb;

    private void OnThumbDown(object sender, MouseButtonEventArgs e)
    {
        if (ThumbOf(sender) is not { } t || Preview.FromButton(e.OriginalSource)) return; // el botón de ampliar no selecciona
        if (e.ClickCount == 2) { OpenThumb(t); e.Handled = true; return; }
        _dragOrigin = e.GetPosition(null);
        _downThumb = t;
        _maybeCollapse = false;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor != null)
        {
            // Rango desde la última foto marcada hasta esta.
            var all = Shown;
            int a = all.IndexOf(_anchor), b = all.IndexOf(t);
            if (a >= 0 && b >= 0)
            {
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _sel.Clear(); foreach (var o in all) o.Selected = false; }
                for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++) SetSelected(all[i], true);
                ShowSelection();
                return;
            }
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SetSelected(t, !t.Selected); _anchor = t; ShowSelection(); }
        else if (!t.Selected) { SelectOnly(t); ShowSelection(); }
        else _maybeCollapse = true; // ya estaba marcada: si solo es un clic, se queda ella sola; si se arrastra, van todas las marcadas
    }

    // Teclado: Espacio marca o desmarca (como Ctrl+clic) e Intro abre la foto.
    private void OnThumbKey(object sender, KeyEventArgs e)
    {
        if (ThumbOf(sender) is not { } t) return;
        if (e.Key == Key.Space) { SetSelected(t, !t.Selected); _anchor = t; ShowSelection(); e.Handled = true; }
        else if (e.Key == Key.Enter) { OpenThumb(t); e.Handled = true; }
    }

    private void OnThumbExpand(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ThumbOf(sender) is { } t) OpenThumb(t);
    }

    private void OnThumbUp(object sender, MouseButtonEventArgs e)
    {
        if (_maybeCollapse && _downThumb != null) SelectOnly(_downThumb);
        _downThumb = null;
    }

    private void SelectOnly(Thumb t)
    {
        _sel.Clear();
        foreach (var other in Shown) other.Selected = false;
        SetSelected(t, true);
        _anchor = t;
    }

    private void OnThumbMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _downThumb == null || _busy) return;
        var d = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var drag = SelectedItems;
        if (_downThumb.Item is { } one && !drag.Contains(one)) drag = new List<AlbumItem> { one };
        _downThumb = null;
        _maybeCollapse = false;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DragFormat, drag), DragDropEffects.Move);
    }

    private void OnThumbRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ThumbOf(sender) is { } t && !t.Selected) SelectOnly(t);
        if (sender is FrameworkElement fe && fe.ContextMenu == null)
        {
            var menu = new ContextMenu();
            AddItem(menu, "Ver en grande", OnThumbOpen);
            AddItem(menu, "Live Photo: ver el clip y elegir fotograma…", OnThumbLive);
            AddItem(menu, "Abrir con el visor de Windows", OnThumbExternal);
            AddItem(menu, "Quitar del álbum (volver a su carpeta por fecha)", OnRemoveFromAlbum);
            AddItem(menu, "Descartar (apartar a «_Descartadas»)…", OnDiscardSelected);
            fe.ContextMenu = menu;
        }
    }

    private static void AddItem(ContextMenu menu, string header, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        menu.Items.Add(item);
    }

    private void OnThumbOpen(object sender, RoutedEventArgs e)
    {
        if (Shown.FirstOrDefault(t => t.Selected) is { } t) OpenThumb(t);
    }

    /// <summary>Vista grande dentro de la app, con anterior/siguiente entre las miniaturas que se están viendo.</summary>
    private void OpenThumb(Thumb t)
    {
        var all = Shown;
        var photos = all.Select(x => new PreviewPhoto(x.Path, System.IO.Path.GetFileName(x.Relative),
            (x.Item != null ? $"{x.Item.Date:d MMM yyyy HH:mm}" : "") + (x.LiveClip != null ? " · Live Photo (foto fija)" : ""))).ToList();
        Preview.Open(this, photos, Math.Max(0, all.IndexOf(t)));
    }

    private void OnThumbLive(object sender, RoutedEventArgs e)
    {
        if (Shown.FirstOrDefault(t => t.Selected) is not { } t) return;
        if (t.LiveClip == null) { StatusText.Text = "Esa foto no es un Live Photo."; return; }
        new LivePhotoWindow(_log, _folder, t.Relative, t.LiveClip) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnThumbExternal(object sender, RoutedEventArgs e)
    {
        if (Shown.FirstOrDefault(t => t.Selected) is { } t) Process.Start(new ProcessStartInfo(t.Path) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ Álbumes propios

    private void OnNewAlbum(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_folder)) { StatusText.Text = "La carpeta de fotos todavía no existe."; return; }
        var prompt = new PromptWindow { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true || prompt.Result == null) return;
        try
        {
            var name = AlbumService.Create(_folder, prompt.Result);
            RefreshAlbums();
            AlbumList.SelectedIndex = AlbumList.Items.OfType<AlbumEntry>().ToList().FindIndex(a => a.Name == name);
            StatusText.Text = $"Álbum «{name}» creado. Arrastra fotos hasta él.";
        }
        catch (Exception ex) { StatusText.Text = "No se pudo crear el álbum: " + ex.Message; }
    }

    private ListBoxItem? _dropHover;

    private ListBoxItem? AlbumItemUnder(DragEventArgs e)
        => e.OriginalSource is DependencyObject d ? ItemsControl.ContainerFromElement(AlbumList, d) as ListBoxItem : null;

    private void ClearDropHover()
    {
        _dropHover?.ClearValue(BackgroundProperty);
        _dropHover = null;
    }

    private void OnAlbumDragOver(object sender, DragEventArgs e)
    {
        var item = AlbumItemUnder(e);
        var ok = e.Data.GetDataPresent(DragFormat) && !_busy && item?.DataContext is AlbumEntry { IsInbox: false };
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (_dropHover != item) ClearDropHover();
        if (ok && item != null) { item.Background = (Brush)FindResource("AccentTint"); _dropHover = item; }
        e.Handled = true;
    }

    private void OnAlbumDragLeave(object sender, DragEventArgs e) => ClearDropHover();

    private async void OnAlbumDrop(object sender, DragEventArgs e)
    {
        var item = AlbumItemUnder(e);
        ClearDropHover();
        e.Handled = true;
        if (item?.DataContext is not AlbumEntry album || e.Data.GetData(DragFormat) is not List<AlbumItem> dragged || _busy) return;
        if (album.IsInbox) { StatusText.Text = "«Nuevos» es la bandeja de las fotos recién descargadas: arrástralas desde ahí a uno de tus álbumes."; return; }
        var inAlbum = System.IO.Path.Combine(AlbumService.Folder, album.Name) + System.IO.Path.DirectorySeparatorChar;
        var items = dragged
            .Where(i => !i.Media.RelativePath.StartsWith(inAlbum, StringComparison.OrdinalIgnoreCase)).ToList();
        if (items.Count == 0) { StatusText.Text = $"Esas fotos ya están en «{album.Name}»."; return; }
        await MoveAsync(items, album.Name, $"{N(items.Count, "foto añadida", "fotos añadidas")} al álbum «{album.Name}».");
    }

    private async void OnRemoveFromAlbum(object sender, RoutedEventArgs e)
    {
        var prefix = AlbumService.Folder + System.IO.Path.DirectorySeparatorChar;
        var items = SelectedItems
            .Where(i => i.Media.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (items.Count == 0) { StatusText.Text = "Esas fotos no están en un álbum propio."; return; }
        await MoveAsync(items, null, $"{N(items.Count, "foto devuelta", "fotos devueltas")} a su carpeta por fecha.");
    }

    private async Task MoveAsync(List<AlbumItem> items, string? album, string done)
    {
        if (_busy) return;
        using var gate = LibraryGate.TryEnter("mover fotos de álbum");
        if (gate == null) { StatusText.Text = LibraryGate.BusyMessage; return; }
        _busy = true;
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        StatusText.Text = "Moviendo fotos…";
        _restoreAlbum = _currentAlbum;
        _restoreGroup = _currentAlbum == null ? _group?.Name : null;
        try
        {
            var folder = _folder;
            var result = await Task.Run(() =>
            {
                using var store = FileMover.OpenExistingManifest(folder);
                var svc = new AlbumService(_log);
                return album != null ? svc.Add(folder, store, album, items) : svc.Remove(folder, store, items);
            });
            await LoadAsync(_folder);
            StatusText.Text = result.Failed.Count == 0 ? done : $"{done} {result.Failed.Count} con error (mira el registro).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "No se pudieron mover: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally
        {
            _busy = false;
            Bar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAlbumRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(AlbumList, d) is not ListBoxItem item) return;
        item.IsSelected = true;
        if (item.ContextMenu == null)
        {
            var menu = new ContextMenu();
            AddItem(menu, "Cambiar nombre…", OnAlbumRename);
            AddItem(menu, "Abrir carpeta en el Explorador", OnAlbumOpenFolder);
            AddItem(menu, "Eliminar álbum (solo si está vacío)", OnAlbumDelete);
            item.ContextMenu = menu;
        }
    }

    private async void OnAlbumRename(object sender, RoutedEventArgs e)
    {
        if (AlbumList.SelectedItem is not AlbumEntry album || _busy) return;
        if (album.IsInbox) { StatusText.Text = "«Nuevos» no se puede renombrar: es donde caen las fotos nuevas del iPhone."; return; }
        var prompt = new PromptWindow("Cambiar nombre del álbum", album.Name, "Cambiar nombre") { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true || prompt.Result == null) return;
        var newName = prompt.Result;
        using var gate = LibraryGate.TryEnter("renombrar álbum");
        if (gate == null) { StatusText.Text = LibraryGate.BusyMessage; return; }
        _busy = true;
        try
        {
            var folder = _folder;
            var name = await Task.Run(() =>
            {
                using var store = FileMover.OpenExistingManifest(folder); // las fotos del álbum cambian de ruta: se anota en el manifiesto
                return new AlbumService(_log).Rename(folder, store, album.Name, newName);
            });
            _restoreAlbum = name;
            await LoadAsync(_folder);
            StatusText.Text = name == album.Name ? "El nombre es el mismo." : $"Álbum «{album.Name}» renombrado a «{name}».";
        }
        catch (Exception ex)
        {
            StatusText.Text = "No se pudo cambiar el nombre: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally { _busy = false; }
    }

    private void OnAlbumOpenFolder(object sender, RoutedEventArgs e)
    {
        if (AlbumList.SelectedItem is AlbumEntry a)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{System.IO.Path.Combine(_folder, a.RelativeFolder)}\"") { UseShellExecute = true });
    }

    private void OnAlbumDelete(object sender, RoutedEventArgs e)
    {
        if (AlbumList.SelectedItem is not AlbumEntry a) return;
        if (a.IsInbox) { StatusText.Text = "«Nuevos» no se elimina: se vacía repartiendo sus fotos."; return; }
        if (!AlbumService.DeleteIfEmpty(_folder, a.Name)) { StatusText.Text = $"«{a.Name}» no está vacío: quita antes sus fotos (clic derecho en una foto)."; return; }
        RefreshAlbums();
        StatusText.Text = $"Álbum «{a.Name}» eliminado.";
        if (_groups.Count > 0) GroupList.SelectedIndex = 0;
    }

    // ------------------------------------------------------------------ Agrupar por fecha o lugar

    private void OnByDate(object sender, RoutedEventArgs e) => SetMode(false);
    private void OnByPlace(object sender, RoutedEventArgs e) => SetMode(true);

    private void SetMode(bool byPlace)
    {
        _byPlace = byPlace;
        ByDateButton.Style = (Style)FindResource(byPlace ? "SecondaryButton" : "PrimaryButton");
        ByPlaceButton.Style = (Style)FindResource(byPlace ? "PrimaryButton" : "SecondaryButton");
        ShowGroups();
    }

    private async void OnDownloadPlaces(object sender, RoutedEventArgs e)
    {
        DownloadPlacesButton.IsEnabled = false;
        StatusText.Text = "Descargando nombres de lugares…";
        try
        {
            await PlaceFinder.DownloadAsync(_folder, CancellationToken.None);
            await LoadAsync(_folder);
        }
        catch (Exception ex)
        {
            StatusText.Text = "No se pudo descargar: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally { DownloadPlacesButton.IsEnabled = true; }
    }
}
