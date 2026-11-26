using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhonePhotos.Core.Organize;

namespace IPhonePhotos.App;

/// <summary>
/// Vista grande de una foto del conjunto que se estaba viendo: ajustada a la ventana, con zoom (rueda, + / −, «100 %»),
/// ← → para pasar de una a otra, F11 para pantalla completa y Esc para cerrar. Carga fuera del hilo de la interfaz y suelta
/// la imagen al cerrar. Solo lee: no toca los archivos. En «Repetidas» permite elegir con cuál quedarse.
/// La lógica de navegación y zoom está en <see cref="PreviewNavigator"/> (Core, con pruebas).
/// </summary>
public partial class PhotoPreviewWindow : Window
{
    private readonly IReadOnlyList<PreviewPhoto> _photos;
    private readonly PreviewNavigator _nav;
    private readonly Action<int>? _pick;
    private readonly Func<int, bool>? _isPicked;
    private int _version; // evita que una carga lenta pise a una foto más reciente
    private WindowState _beforeFull;

    public PhotoPreviewWindow(IReadOnlyList<PreviewPhoto> photos, int index, Action<int>? pick = null, Func<int, bool>? isPicked = null)
    {
        InitializeComponent();
        _photos = photos;
        _nav = new PreviewNavigator(photos.Count, index);
        _pick = pick;
        _isPicked = isPicked;
        PickButton.Visibility = pick == null ? Visibility.Collapsed : Visibility.Visible;
        Viewer.SizeChanged += (_, _) => ApplyZoom();
        Closed += (_, _) => { _version++; Big.Source = null; }; // suelta la imagen (la memoria la recupera el recolector)
        Loaded += (_, _) => Show();
    }

    private async void Show()
    {
        var photo = _photos[_nav.Index];
        var version = ++_version;
        Title = $"{photo.Name} · {_nav.Position}";
        NameText.Text = photo.Name;
        InfoText.Text = $"{photo.Info} · {_nav.Position}";
        ExtraText.Text = photo.Extra ?? "";
        ExtraText.Visibility = string.IsNullOrEmpty(photo.Extra) ? Visibility.Collapsed : Visibility.Visible;
        if (_pick != null)
        {
            var picked = _isPicked?.Invoke(_nav.Index) == true;
            PickButton.Content = picked ? "Es la que me quedo" : "Quedarme con esta";
            PickButton.IsEnabled = !picked;
        }
        Loading.Text = "Cargando…";
        Loading.Visibility = Visibility.Visible;
        Big.Source = null;
        // Tamaño real hasta 8000 px de ancho (para que «100 %» sea de verdad en fotos de iPhone); HEIC necesita la extensión de Windows.
        var image = await Task.Run(() => Decode(photo.Path));
        if (version != _version) return;
        Big.Source = image;
        ApplyZoom();
        if (image == null) Loading.Text = "No se puede mostrar esta foto (¿falta «Extensiones de imagen HEIF» de Windows o el archivo está dañado?).";
        Loading.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BitmapImage? Decode(string path)
    {
        var full = ThumbLoader.Decode(path, 0);
        return full is { PixelWidth: > 8000 } ? ThumbLoader.Decode(path, 8000) : full;
    }

    // ------------------------------------------------------------------ Zoom

    /// <summary>Escala con la que se ve la foto ajustada (respecto a su tamaño real).</summary>
    private double FitScale()
    {
        if (Big.Source is not BitmapSource b || b.PixelWidth == 0) return 1;
        var w = Math.Max(1, Viewer.ActualWidth - 16);
        var h = Math.Max(1, Viewer.ActualHeight - 16);
        return Math.Min(1, Math.Min(w / b.PixelWidth, h / b.PixelHeight));
    }

    private void ApplyZoom()
    {
        if (Big.Source is not BitmapSource b) return;
        if (_nav.Zoom is not { } z)
        {
            Big.Stretch = Stretch.Uniform;
            Big.Width = Big.Height = double.NaN;
            Big.MaxWidth = b.PixelWidth; // ajustada, pero sin agrandar una foto pequeña
            Big.MaxHeight = b.PixelHeight;
            Viewer.HorizontalScrollBarVisibility = Viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ZoomText.Content = "Ajustar";
            return;
        }
        // Píxeles reales → unidades de WPF según la escala de pantalla, para que 100 % sea un píxel de la foto por píxel de pantalla.
        var dpi = VisualTreeHelper.GetDpi(this);
        Big.Stretch = Stretch.Fill;
        Big.MaxWidth = Big.MaxHeight = double.PositiveInfinity;
        Big.Width = b.PixelWidth * z / dpi.DpiScaleX;
        Big.Height = b.PixelHeight * z / dpi.DpiScaleY;
        Viewer.HorizontalScrollBarVisibility = Viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ZoomText.Content = $"{z * 100:0} %";
    }

    private void ZoomIn() { _nav.ZoomIn(FitScale()); ApplyZoom(); }
    private void ZoomOut() { _nav.ZoomOut(FitScale()); ApplyZoom(); }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) ZoomIn(); else ZoomOut();
        e.Handled = true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomIn();
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomOut();
    private void OnActual(object sender, RoutedEventArgs e) { _nav.ActualSize(); ApplyZoom(); }
    private void OnFit(object sender, RoutedEventArgs e) { _nav.Fit(); ApplyZoom(); }

    // ------------------------------------------------------------------ Navegación y ventana

    private void OnPrev(object sender, RoutedEventArgs e) { _nav.Previous(); Show(); }
    private void OnNext(object sender, RoutedEventArgs e) { _nav.Next(); Show(); }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        _pick?.Invoke(_nav.Index);
        Show();
    }

    private void OnFull(object sender, RoutedEventArgs e) => ToggleFull();

    private void ToggleFull()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _beforeFull;
            FullButton.Content = "Pantalla completa";
        }
        else
        {
            _beforeFull = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal; // cambiar a Maximized después hace que cubra también la barra de tareas
            WindowState = WindowState.Maximized;
            FullButton.Content = "Salir de pantalla completa";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: _nav.Previous(); Show(); break;
            case Key.Right: _nav.Next(); Show(); break;
            case Key.Add or Key.OemPlus: ZoomIn(); break;
            case Key.Subtract or Key.OemMinus: ZoomOut(); break;
            case Key.D0 or Key.NumPad0: _nav.Fit(); ApplyZoom(); break;
            case Key.D1 or Key.NumPad1: _nav.ActualSize(); ApplyZoom(); break;
            case Key.F11: ToggleFull(); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }
}
