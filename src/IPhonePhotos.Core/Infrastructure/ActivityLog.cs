namespace IPhonePhotos.Core;

public enum LogLevel { Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}";
}

/// <summary>Registro de actividad: avisa a la interfaz y, si se le indica, escribe un archivo de log.</summary>
public sealed class ActivityLog
{
    private readonly object _gate = new();
    private string? _filePath;

    public event Action<LogEntry>? Entry;

    /// <summary>Empieza a escribir además en un archivo (normalmente dentro de la carpeta de destino).</summary>
    public void AttachFile(string path)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _filePath = path;
            }
            catch
            {
                _filePath = null; // el log a archivo es opcional: nunca debe romper la copia
            }
        }
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    private void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_gate)
        {
            if (_filePath != null)
            {
                try { File.AppendAllText(_filePath, entry + Environment.NewLine); }
                catch { /* ignorar errores de disco en el log */ }
            }
        }
        Entry?.Invoke(entry);
    }
}
