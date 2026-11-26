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
        Checked(Folders().Select(d => Path.Combine(d, fileName)).FirstOrDefault(File.Exists));

    public static string? FindContaining(string part) =>
        Checked(Folders().Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.onnx"))
            .FirstOrDefault(f => Path.GetFileName(f).Contains(part, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// SHA-256 de los modelos de los paquetes NuGet que indica el README. Decisión: un archivo con uno de estos nombres y
    /// otro contenido NO se carga (ONNX Runtime analizaría un archivo manipulado); un modelo con otro nombre (otro
    /// arcface*.onnx) se usa, pero su hash queda en <see cref="Notice"/> para que se vea y se pueda comprobar.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["scrfd_2.5g_kps.onnx"] = "041F73F47371333D1D17A6FEE6C8AB4E6AECABEFE398FF32CCA4E2D5EAEE0AF9",
        ["arcfaceresnet100-11-int8.onnx"] = "C625CA68A422418C48AA84F73341337E0A92B111F327909005D1EEC07C95F936",
        ["open_closed_eye.onnx"] = "4DAA100034482525A26C9AFB9297C16580A531189E66E3D2B2AC7D32BECFD593",
    };

    /// <summary>Último aviso sobre un modelo rechazado o desconocido (null si todos coinciden).</summary>
    public static string? Notice { get; private set; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, DateTime), string> HashCache = new();

    /// <summary>La ruta si el modelo se puede usar; null si su nombre es conocido y el contenido no coincide.</summary>
    public static string? Checked(string? path)
    {
        if (path == null) return null;
        var info = new FileInfo(path);
        var hash = HashCache.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), _ =>
        {
            using var s = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
        });
        var name = info.Name;
        if (KnownHashes.TryGetValue(name, out var expected))
        {
            if (hash == expected) return path;
            Notice = $"«{name}» no coincide con el modelo conocido (SHA-256 {hash}) y no se usa. Vuelve a copiarlo del paquete NuGet que indica el README.";
            return null;
        }
        Notice = $"«{name}» no está en la lista de modelos conocidos; se usa igual. SHA-256: {hash}.";
        return path;
    }

    public static string MissingMessage =>
        $"Faltan los modelos de caras. La app no los incluye por su licencia (no comercial): cópialos en «{Folder}» " +
        "(scrfd_2.5g_kps.onnx, un modelo arcface*.onnx y open_closed_eye.onnx). El README explica de dónde sacarlos." +
        (Notice != null ? " " + Notice : "");
}
