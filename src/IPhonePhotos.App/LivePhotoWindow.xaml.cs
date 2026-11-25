using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Conversion;

namespace IPhonePhotos.App;

/// <summary>
/// Un Live Photo como en el iPhone: se reproduce el clip y se puede parar en cualquier instante. Luego se guarda como JPG
/// ese fotograma o, si se prefiere, la foto original (el original nunca se modifica). Los JPG van a la carpeta
/// «&lt;álbum&gt; (JPG)\Live Photos».
/// shortcut: el instante de la foto original se aproxima por la mitad del clip (el iPhone graba ~1,5 s antes y ~1,5 s después);
/// el valor exacto está en los metadatos «still-image-time» del .MOV y no se lee todavía.
/// </summary>
public partial class LivePhotoWindow : Window
{
    public const string OutputFolder = "Live Photos (JPG)";
    private readonly ActivityLog _log;
    private readonly string _root, _photo, _clip;
    private LiveFrameGrabber? _grabber;
    private bool _playing, _updating;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private int _photoW, _photoH;

    public LivePhotoWindow(ActivityLog log, string root, string photoRelative, string clipRelative)
    {
        InitializeComponent();
        _log = log;
        _root = root;
        _photo = Path.Combine(root, photoRelative);
        _clip = Path.Combine(root, clipRelative);
        NameText.Text = Path.GetFileName(photoRelative);
        try
        {
            using var s = File.OpenRead(_photo);
            var f = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            (_photoW, _photoH) = (f.PixelWidth, f.PixelHeight);
        }
        catch { /* sin extensión HEIF: no se puede comparar el tamaño */ }
        Player.Source = new Uri(_clip);
        Player.Play();
        Player.Pause(); // arranca para que pinte el primer fotograma
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => { if (_playing) SetSlider(Player.Position.TotalSeconds); };
        Closing += (_, _) => { _timer?.Stop(); Player.Close(); _grabber?.Dispose(); };
    }

    private double Duration => Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;

    private void OnOpened(object sender, RoutedEventArgs e)
    {
        Seek.Maximum = Math.Max(Duration, 0.01);
        Seek.IsEnabled = true;
        OnKey(sender, e);
        InfoText.Text = $"Clip: {Player.NaturalVideoWidth}×{Player.NaturalVideoHeight}, {Duration:0.0} s" +
                        (_photoW > 0 ? $" · Foto original: {_photoW}×{_photoH}. Un fotograma suele tener menos resolución que la foto original." : "");
    }

    private void OnFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        StatusText.Text = "Windows no puede abrir el clip. Hace falta la «Extensión de vídeo HEVC» de Windows (Microsoft Store). " + e.ErrorException?.Message;
        _log.Warn("Live Photo: " + e.ErrorException?.Message);
    }

    private void OnEnded(object sender, RoutedEventArgs e)
    {
        _playing = false;
        _timer?.Stop();
        PlayButton.Content = "Reproducir";
        Player.Position = TimeSpan.Zero;
        Player.Pause();
        SetSlider(0);
    }

    private void SetSlider(double seconds)
    {
        _updating = true;
        Seek.Value = Math.Min(seconds, Seek.Maximum);
        TimeText.Text = $"{Seek.Value:0.00} s";
        _updating = false;
    }

    private void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !Seek.IsEnabled) return;
        if (_playing) { _playing = false; _timer?.Stop(); Player.Pause(); PlayButton.Content = "Reproducir"; }
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
        TimeText.Text = $"{Seek.Value:0.00} s";
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_playing) { _playing = false; _timer?.Stop(); Player.Pause(); PlayButton.Content = "Reproducir"; return; }
        _playing = true;
        PlayButton.Content = "Pausa";
        _timer?.Start();
        Player.Play();
    }

    private void OnKey(object sender, RoutedEventArgs e)
    {
        if (_playing) OnPlay(sender, e);
        SetSlider(Duration / 2);
        Player.Position = TimeSpan.FromSeconds(Duration / 2);
    }

    private async void OnSaveFrame(object sender, RoutedEventArgs e)
    {
        SaveFrameButton.IsEnabled = false;
        try
        {
            if (_playing) OnPlay(sender, e);
            var at = TimeSpan.FromSeconds(Seek.Value);
            if (_grabber == null)
            {
                _grabber = new LiveFrameGrabber();
                await _grabber.OpenAsync(_clip);
            }
            var frame = await _grabber.GrabAsync(at);
            var dest = Destination($"{Path.GetFileNameWithoutExtension(_photo)} (fotograma {at.TotalSeconds:0.0} s).jpg");
            LiveFrameGrabber.SaveJpeg(frame, dest);
            CopyDates(dest);
            StatusText.Text = $"Guardado: {dest}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "No se pudo guardar el fotograma: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally { SaveFrameButton.IsEnabled = true; }
    }

    private async void OnSaveOriginal(object sender, RoutedEventArgs e)
    {
        SaveOriginalButton.IsEnabled = false;
        try
        {
            var dest = Destination(Path.GetFileNameWithoutExtension(_photo) + ".jpg");
            await Task.Run(() => HeicToJpgService.ConvertOne(_photo, dest, 92));
            CopyDates(dest);
            StatusText.Text = $"Guardado: {dest}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "No se pudo convertir la foto original: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally { SaveOriginalButton.IsEnabled = true; }
    }

    private string Destination(string fileName)
    {
        // Fuera de la carpeta del álbum (como la conversión HEIC→JPG), para que el álbum y «Organizar» no los cuenten como fotos nuevas.
        var dir = Path.Combine(_root.TrimEnd('\\', '/') + " (JPG)", OutputFolder);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, fileName);
        for (var n = 2; File.Exists(dest); n++)
            dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}");
        return dest;
    }

    // Para que se sigan ordenando por fecha en el Explorador como la foto original.
    private void CopyDates(string dest)
    {
        File.SetCreationTime(dest, File.GetCreationTime(_photo));
        File.SetLastWriteTime(dest, File.GetLastWriteTime(_photo));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
