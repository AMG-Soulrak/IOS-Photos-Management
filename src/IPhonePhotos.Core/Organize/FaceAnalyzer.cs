using FaceAiSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IPhonePhotos.Core.Organize;

/// <summary>Lo que se ve de las personas en una foto: cuántas caras, cuántos ojos abiertos y cerrados, y cuántas caras tocan el borde.</summary>
public sealed record FaceStats(int Faces, int OpenEyes, int ClosedEyes, int CutFaces, int Version = 1);

/// <summary>
/// Detecta caras (SCRFD) y mira si los ojos están abiertos (open-closed-eye-0001 de Intel OpenVINO) con FaceAiSharp y ONNX Runtime.
/// Todo se ejecuta en el PC: no se envía ninguna foto a ningún sitio. Los modelos NO vienen con la app (ver <see cref="FaceModels"/>).
/// Solo cuenta caras y ojos para ordenar repetidas: no calcula huellas ni guarda datos de quién es cada persona.
/// shortcut: el modelo de ojos solo se evalúa en ojos de al menos 16 px y no es perfecto (gafas, perfiles y ojos entrecerrados
/// fallan más; precisión citada: 95,85 %), así que solo se usa para ORDENAR las candidatas de un grupo, nunca para decidir solo.
/// </summary>
public sealed class FaceAnalyzer : IDisposable
{
    public const int CurrentVersion = 1;
    private const int AnalysisWidth = 1280;
    private readonly ScrfdDetector _detector;
    private readonly OpenVinoOpenClosedEye0001 _eyes;
    private readonly object _gate = new(); // el detector no está pensado para usarse a la vez desde varios hilos

    private FaceAnalyzer(ScrfdDetector detector, OpenVinoOpenClosedEye0001 eyes)
    {
        _detector = detector;
        _eyes = eyes;
    }

    /// <summary>Null si faltan los modelos o no se pueden cargar (en ese caso las repetidas se puntúan sin caras).</summary>
    public static FaceAnalyzer? TryCreate()
    {
        var scrfd = FaceModels.Find("scrfd_2.5g_kps.onnx");
        var eye = FaceModels.Find("open_closed_eye.onnx");
        if (scrfd == null || eye == null) return null;
        try
        {
            return new FaceAnalyzer(
                new ScrfdDetector(new ScrfdDetectorOptions { ModelPath = scrfd }),
                new OpenVinoOpenClosedEye0001(new OpenVinoOpenClosedEye0001Options { ModelPath = eye }));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Null si la foto no se puede leer.</summary>
    public FaceStats? Analyze(string path)
    {
        try
        {
            using var image = Load(path);
            lock (_gate)
            {
                var faces = _detector.DetectFaces(image);
                var margin = Math.Max(2f, image.Width * 0.01f);
                var cut = faces.Count(f => f.Box.Left <= margin || f.Box.Top <= margin
                                           || f.Box.Right >= image.Width - margin || f.Box.Bottom >= image.Height - margin);
                var eyes = Applications.CountEyeStates(_detector, _eyes, image, 3f);
                return new FaceStats(faces.Count, eyes.OpenEyes, eyes.ClosedEyes, cut);
            }
        }
        catch
        {
            return null;
        }
    }

    // ImageSharp no lee HEIC: se decodifica con Windows (WIC), ya reducida, y se le pasan los píxeles.
    internal static Image<Rgb24> Load(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.DecodePixelWidth = AnalysisWidth;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.EndInit();
        var bgr = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        int w = bgr.PixelWidth, h = bgr.PixelHeight, stride = w * 3;
        var px = new byte[stride * h];
        bgr.CopyPixels(px, stride, 0);
        for (var i = 0; i < px.Length; i += 3) (px[i], px[i + 2]) = (px[i + 2], px[i]); // BGR → RGB
        return Image.LoadPixelData<Rgb24>(px, w, h);
    }

    public void Dispose()
    {
        (_detector as IDisposable)?.Dispose();
        (_eyes as IDisposable)?.Dispose();
    }
}
