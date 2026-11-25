using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IPhonePhotos.Core.Conversion;

/// <summary>
/// Saca fotogramas sueltos de un clip de Live Photo (.MOV) con el reproductor de Windows (Media Foundation), el mismo que
/// reproduce el clip. Los clips de iPhone son HEVC: hace falta la «Extensión de vídeo HEVC» de Windows, que ya trae la mayoría de
/// los equipos con las «Extensiones de imagen HEIF». Funciona en su propio hilo, así que se puede usar desde cualquier sitio.
/// shortcut: tras mover el reproductor se espera un tiempo fijo (<see cref="SeekWait"/>) porque WPF no avisa de que el fotograma
/// ya está pintado; si un equipo lento devuelve el fotograma anterior, habría que subir esa espera.
/// </summary>
public sealed class LiveFrameGrabber : IDisposable
{
    private static readonly TimeSpan SeekWait = TimeSpan.FromMilliseconds(250);
    private readonly Dispatcher _dispatcher;
    private MediaPlayer? _player;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public TimeSpan Duration { get; private set; }

    public LiveFrameGrabber()
    {
        Dispatcher? d = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => { d = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); })
            { IsBackground = true, Name = "live-frames" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = d!;
    }

    public Task OpenAsync(string path) => _dispatcher.InvokeAsync(async () =>
    {
        var opened = new TaskCompletionSource();
        var player = new MediaPlayer { ScrubbingEnabled = true, IsMuted = true };
        player.MediaOpened += (_, _) => opened.TrySetResult();
        player.MediaFailed += (_, e) => opened.TrySetException(new InvalidOperationException(
            "Windows no puede abrir el clip (¿falta la «Extensión de vídeo HEVC»?): " + e.ErrorException?.Message));
        player.Open(new Uri(path));
        if (await Task.WhenAny(opened.Task, Task.Delay(10_000)) != opened.Task)
            throw new TimeoutException("El clip tardó demasiado en abrirse.");
        await opened.Task;
        _player = player;
        Width = player.NaturalVideoWidth;
        Height = player.NaturalVideoHeight;
        Duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        player.Play(); // con ScrubbingEnabled, mover la posición solo pinta el fotograma si el reproductor ha arrancado y se pausa
        player.Pause();
    }).Task.Unwrap();

    /// <summary>El fotograma del instante pedido, a tamaño completo, o reducido a <paramref name="maxWidth"/> si es mayor que 0.</summary>
    public Task<BitmapSource> GrabAsync(TimeSpan at, int maxWidth = 0) => _dispatcher.InvokeAsync(async () =>
    {
        var player = _player ?? throw new InvalidOperationException("Abre primero un clip.");
        player.Position = at;
        await Task.Delay(SeekWait);
        var scale = maxWidth > 0 && Width > maxWidth ? maxWidth / (double)Width : 1;
        int w = Math.Max(1, (int)(Width * scale)), h = Math.Max(1, (int)(Height * scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawVideo(player, new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return (BitmapSource)bitmap;
    }).Task.Unwrap();

    public static void SaveJpeg(BitmapSource frame, string dest, int quality = 92)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = File.Create(dest);
        encoder.Save(output);
    }

    public void Dispose()
    {
        try { _dispatcher.Invoke(() => _player?.Close()); } catch { }
        _dispatcher.InvokeShutdown();
    }
}
