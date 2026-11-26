using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IPhonePhotos.App;

/// <summary>Un cuadro con miniatura: la imagen se decodifica en segundo plano y aparece cuando está lista.</summary>
public sealed class Thumb : INotifyPropertyChanged
{
    private ImageSource? _source;
    public string Path { get; init; } = "";
    public string Tip { get; init; } = "";
    public string Relative { get; init; } = "";
    public string? LiveClip { get; init; }
    public IPhonePhotos.Core.Organize.AlbumItem? Item { get; init; }
    /// <summary>Etiqueta de abajo: el álbum o la carpeta donde está la foto.</summary>
    public string Where { get; init; } = "";
    public Brush WhereBack { get; init; } = Brushes.Transparent;
    private bool _selected;
    /// <summary>Marcada para arrastrarla a un álbum o quitarla de él.</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
            PropertyChanged?.Invoke(this, new(nameof(CheckVisibility)));
            PropertyChanged?.Invoke(this, new(nameof(A11yName)));
        }
    }
    /// <summary>Marca ✓ visible de las fotos marcadas (además del borde de color).</summary>
    public Visibility CheckVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Lo que lee el lector de pantalla: la foto y si está marcada.</summary>
    public string A11yName => (string.IsNullOrEmpty(Tip) ? System.IO.Path.GetFileName(Relative) : Tip) + (_selected ? ", marcada" : "");
    /// <summary>Nombre del botón de ampliar para el lector de pantalla.</summary>
    public string ExpandName => "Ampliar foto " + System.IO.Path.GetFileName(string.IsNullOrEmpty(Relative) ? Path : Relative);
    public Visibility LiveVisibility => LiveClip == null ? Visibility.Collapsed : Visibility.Visible;
    public ImageSource? Source { get => _source; set { _source = value; PropertyChanged?.Invoke(this, new(nameof(Source))); } }
    /// <summary>Ya se pidió decodificarla (la cuadrícula del álbum suelta las que quedan lejos de la vista).</summary>
    public bool Requested { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class ThumbLoader
{
    private static readonly SemaphoreSlim Gate = new(4);

    /// <summary>Decodifica la miniatura (como mucho 4 a la vez) y la pone en <paramref name="target"/>.</summary>
    public static async Task LoadAsync(Thumb target, CancellationToken ct, int width = 0)
    {
        try
        {
            if (width <= 0) width = GridWidth(); // en la UI: la cuadrícula muestra 150 px independientes de la densidad, no hace falta más
            await Gate.WaitAsync(ct);
            try { target.Source = await Task.Run(() => Decode(target.Path, width), ct); }
            finally { Gate.Release(); }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Ancho de decodificación de la cuadrícula: lo justo para 150 px con la escala de pantalla actual (a 100 % ≈ 4 veces menos memoria que 300).</summary>
    private static int GridWidth()
    {
        try { return (int)Math.Clamp(Math.Ceiling(150 * VisualTreeHelper.GetDpi(Application.Current.MainWindow).DpiScaleX), 160, 300); }
        catch { return 300; }
    }

    public static BitmapImage? Decode(string path, int width)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path);
            b.DecodePixelWidth = width;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; } // sin extensión HEIF de Windows o archivo dañado: el cuadro queda vacío
    }
}
