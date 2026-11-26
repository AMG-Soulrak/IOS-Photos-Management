namespace IPhonePhotos.Core;

/// <summary>
/// Quita del texto de un error lo que identifica a la persona antes de guardarlo en iphotos-error.log (que se suele
/// enviar al pedir ayuda): la carpeta temporal, la carpeta del perfil y el nombre de usuario de Windows.
/// Límite: rutas fuera del perfil (p. ej. D:\Fotos\…) y nombres de archivo se conservan, porque sin ellos el error no se entiende.
/// </summary>
public static class LogPrivacy
{
    public static string Anonymize(string text) => Anonymize(text,
        Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);

    public static string Anonymize(string text, string tempPath, string profile, string userName)
    {
        string Cut(string s, string what, string with) =>
            what.Length < 3 ? s : s.Replace(Path.TrimEndingDirectorySeparator(what), with, StringComparison.OrdinalIgnoreCase);
        text = Cut(text, tempPath, "%TEMP%");
        text = Cut(text, profile, "%USERPROFILE%");
        return Cut(text, userName, "<usuario>");
    }
}
