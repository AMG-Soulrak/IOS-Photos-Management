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
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x5F, 0xB8)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    /// <summary>Marcada para arrastrarla a un álbum o quitarla de él.</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
            PropertyChanged?.Invoke(this, new(nameof(Outline)));
            PropertyChanged?.Invoke(this, new(nameof(A11yName)));
        }
    }
    public Brush Outline => _selected ? AccentBrush : Brushes.Transparent;
    /// <summary>Lo que lee el lector de pantalla: la foto y si está marcada (la marca solo se ve por el color del borde).</summary>
    public string A11yName => (string.IsNullOrEmpty(Tip) ? System.IO.Path.GetFileName(Relative) : Tip) + (_selected ? ", marcada" : "");
    public Visibility LiveVisibility => LiveClip == null ? Visibility.Collapsed : Visibility.Visible;
    public ImageSource? Source { get => _source; set { _source = value; PropertyChanged?.Invoke(this, new(nameof(Source))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class ThumbLoader
{
    private static readonly SemaphoreSlim Gate = new(4);

    /// <summary>Decodifica la miniatura (como mucho 4 a la vez) y la pone en <paramref name="target"/>.</summary>
    public static async Task LoadAsync(Thumb target, CancellationToken ct, int width = 300)
    {
        try
        {
            await Gate.WaitAsync(ct);
            try { target.Source = await Task.Run(() => Decode(target.Path, width), ct); }
            finally { Gate.Release(); }
        }
        catch (OperationCanceledException) { }
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
