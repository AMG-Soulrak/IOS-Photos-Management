namespace IPhonePhotos.Core.Organize;

/// <summary>
/// Dónde están los modelos de caras (SCRFD, ArcFace y ojos abiertos/cerrados). Los de insightface tienen licencia
/// «non-commercial research only»: la app NO los incluye ni los redistribuye; cada usuario los pone en
/// %LOCALAPPDATA%\IPhonePhotos\models (ver README). Para quien compila, también se buscan en la carpeta «onnx» junto al
/// programa, donde los deja la restauración de NuGet en las compilaciones de desarrollo.
/// </summary>
public static class FaceModels
{
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPhonePhotos", "models");

    private static IEnumerable<string> Folders() => new[] { Folder, Path.Combine(AppContext.BaseDirectory, "onnx") };

    public static string? Find(string fileName) =>
        Folders().Select(d => Path.Combine(d, fileName)).FirstOrDefault(File.Exists);

    public static string? FindContaining(string part) =>
        Folders().Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.onnx"))
            .FirstOrDefault(f => Path.GetFileName(f).Contains(part, StringComparison.OrdinalIgnoreCase));

    public static string MissingMessage =>
        $"Faltan los modelos de caras. La app no los incluye por su licencia (no comercial): cópialos en «{Folder}» " +
        "(scrfd_2.5g_kps.onnx, un modelo arcface*.onnx y open_closed_eye.onnx). El README explica de dónde sacarlos.";
}
