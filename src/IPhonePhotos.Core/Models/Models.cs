namespace IPhonePhotos.Core.Models;

/// <summary>Identifica un dispositivo (iPhone/iPad) visible por una técnica concreta.</summary>
public sealed record DeviceDescriptor(
    string Id,
    string Name,
    string Manufacturer,
    string Model,
    string SerialNumber,
    string Technique,
    bool IsSnapshot = false,
    string Detail = "")
{
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name :
        !string.IsNullOrWhiteSpace(Model) ? Model : Id;

    /// <summary>
    /// Número de serie / UDID normalizado (minúsculas, sin guiones ni espacios). Es lo que permite reconocer
    /// que el mismo iPhone visto por dos técnicas distintas (WPD, AFC, copia de seguridad) es un único aparato.
    /// </summary>
    public string SerialKey => NormalizeSerial(SerialNumber);

    public static string NormalizeSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return "";
        return new string(serial.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>Mismo aparato si coincide el Id (misma técnica) o el número de serie normalizado.</summary>
    public bool IsSameDevice(DeviceDescriptor other)
    {
        if (string.Equals(Technique, other.Technique, StringComparison.Ordinal) &&
            string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase)) return true;
        var a = SerialKey;
        return a.Length > 0 && string.Equals(a, other.SerialKey, StringComparison.Ordinal);
    }
}

/// <summary>Qué sabe hacer una técnica de conexión (se usa para elegir la mejor).</summary>
public sealed record BackendCapabilities(
    bool CanList,
    bool CanRead,
    bool ReportsDates,
    bool ReportsSizes);

/// <summary>Un archivo del dispositivo.</summary>
/// <param name="RemotePath">Ruta completa en el dispositivo (para leer).</param>
/// <param name="RelativePath">Ruta relativa que se reproduce en el PC (p. ej. DCIM\100APPLE\IMG_0001.HEIC).</param>
/// <param name="Size">Tamaño en bytes (0 si el dispositivo no lo informa).</param>
/// <param name="Modified">Fecha del archivo según el dispositivo, si la informa.</param>
public sealed record RemoteFile(string RemotePath, string RelativePath, long Size, DateTime? Modified);

public enum FileState
{
    /// <summary>Visto en el dispositivo, pendiente de descargar.</summary>
    Discovered,
    /// <summary>Descarga en curso (si ves esto tras un corte, se reintenta).</summary>
    Downloading,
    /// <summary>Copiado al PC por la app (con hash de la copia) y con tamaño correcto.</summary>
    Downloaded,
    /// <summary>Copiado y comprobado por hash (contra lo descargado o, en profundo, contra el iPhone).</summary>
    Verified,
    /// <summary>Heredado de ≤ v1.4 (la app borraba del iPhone); ya no se escribe. Al abrir el manifiesto se migra a <see cref="Verified"/>.</summary>
    [Obsolete("Solo para leer manifiestos de v1.4 o anteriores; ManifestStore.Open lo migra a Verified.")]
    Deleted,
    /// <summary>Falló tras agotar los reintentos.</summary>
    Failed,
    /// <summary>
    /// Ya estaba en la carpeta con el mismo nombre, tamaño y fecha (adopción rápida). Su contenido no se ha comparado con el
    /// iPhone: no se vuelve a descargar, pero NUNCA cuenta como verificado hasta que la verificación a fondo lo contraste.
    /// </summary>
    AdoptedUnverified
}

/// <summary>Fila del manifiesto: lo que sabemos de cada archivo del dispositivo.</summary>
public sealed class FileRecord
{
    public string Id { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string? LocalRelativePath { get; set; }
    public long Size { get; set; }
    public DateTime? Modified { get; set; }
    public FileState State { get; set; }
    public string? Sha256 { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? DownloadedAtUtc { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public bool VerifiedDeep { get; set; }

    /// <summary>Heredado de ≤ v1.4 (fecha en que se borró del iPhone). Ya no se escribe; se conserva para no perder el dato.</summary>
    public DateTime? DeletedAtUtc { get; set; }

    /// <summary>Nota informativa (p. ej. "adoptado (nombre y tamaño)"). No es un error.</summary>
    public string? Note { get; set; }

    /// <summary>Número de serie normalizado del dispositivo del que viene (informativo; el manifiesto es común a todos).</summary>
    public string? DeviceKey { get; set; }

    /// <summary>Técnica con la que se copió (informativo).</summary>
    public string? Technique { get; set; }

    public string EffectiveLocalRelativePath => string.IsNullOrEmpty(LocalRelativePath) ? RelativePath : LocalRelativePath!;

    /// <summary>Hay un archivo en el PC que lo representa (copiado, verificado o adoptado sin verificar): no se vuelve a descargar.</summary>
    public bool IsCopied => State is FileState.Downloaded or FileState.Verified or FileState.AdoptedUnverified;
}

/// <summary>Criterio para decidir que un archivo del iPhone es "el mismo" que uno del manifiesto o del destino.</summary>
public static class FileIdentity
{
    /// <summary>
    /// Las fechas coinciden si difieren menos de 2 segundos, ignorando desfases de zona horaria: cada técnica informa la
    /// hora a su manera (WPD en local, AFC y copias de seguridad en UTC), y las zonas horarias son múltiplos de 15 minutos.
    /// Una fecha que falta NO vale como coincidencia (desde v1.5); solo dos fechas ausentes son «la misma» (mismo registro
    /// visto otra vez por una técnica que no informa fechas).
    /// </summary>
    public static bool DatesMatch(DateTime? a, DateTime? b)
    {
        if (a is null || b is null) return a is null && b is null;
        var seconds = Math.Abs((a.Value - b.Value).TotalSeconds);
        var rest = seconds % 900; // 15 minutos
        return rest <= 2 || rest >= 898;
    }

    public static string MakeId(string relativePath, long size, DateTime? modified)
    {
        var secs = modified is null ? 0 : modified.Value.Ticks / TimeSpan.TicksPerSecond;
        return $"{relativePath.ToLowerInvariant()}|{size}|{secs}";
    }
}

public sealed class DeviceLostException : Exception
{
    public DeviceLostException(string message, Exception? inner = null) : base(message, inner) { }
}

public enum AdoptMode
{
    /// <summary>No reutilizar archivos que ya estén en el destino sin constar en el manifiesto.</summary>
    None,
    /// <summary>Un archivo existente con el mismo nombre, tamaño y fecha no se vuelve a descargar (rápido); queda «adoptado, sin verificar».</summary>
    Fast,
    /// <summary>Además se relee el archivo del iPhone y se compara su contenido (lento, fiable).</summary>
    Deep
}

public sealed class TransferOptions
{
    public string DestinationRoot { get; set; } = "";
    public AdoptMode Adopt { get; set; } = AdoptMode.Fast;
    public int Retries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>Guardar las fotos nuevas en carpetas Año\Mes (según su fecha en el iPhone) en vez de reproducir DCIM\100APPLE.</summary>
    public bool OrganizeByDate { get; set; }

    /// <summary>
    /// Carpeta (dentro del destino) donde van las fotos NUEVAS, para repartirlas después en álbumes. Null: se usa el reparto
    /// de siempre (DCIM\100APPLE o Año\Mes). Lo que ya está en la biblioteca, en la carpeta que sea, no se vuelve a descargar.
    /// </summary>
    public string? NewFolder { get; set; }

    /// <summary>Cuánto esperar a que el iPhone vuelva si se desconecta a mitad de la copia.</summary>
    public TimeSpan ReconnectTimeout { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan ReconnectPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Si no es null, usa solo esa técnica (clave: wpd, afc, backup). Útil para diagnosticar.</summary>
    public string? OnlyTechnique { get; set; }

    /// <summary>
    /// Si ninguna técnica en vivo sirve (o quedan archivos que no se pudieron copiar), permite leer la última
    /// copia de seguridad de ese mismo iPhone. Ojo: es una foto fija; las fotos posteriores no estarán.
    /// </summary>
    public bool AllowSnapshotFallback { get; set; } = true;

    /// <summary>Tiempo máximo para abrir una técnica antes de pasar a la siguiente.</summary>
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(45);
}

/// <param name="AdoptPath">Ruta relativa, en la biblioteca, del archivo que podría ser este mismo (si <paramref name="AdoptCandidate"/>).</param>
public sealed record PendingItem(RemoteFile Remote, FileRecord Record, bool AdoptCandidate, string? AdoptPath = null);

public sealed class ScanResult
{
    public List<RemoteFile> All { get; } = new();
    public List<PendingItem> Pending { get; } = new();
    public int AlreadyDone { get; set; }
    public long AlreadyDoneBytes { get; set; }
    public int Adopted { get; set; }
    public int MissingOnDevice { get; set; }

    /// <summary>Archivos rechazados porque su ruta saldría de la carpeta de destino (copia o dispositivo manipulados).</summary>
    public int Rejected { get; set; }
    public long TotalBytes { get; set; }
    public long PendingBytes { get; set; }
}

public sealed class TransferSummary
{
    public int Copied { get; set; }
    public int Adopted { get; set; }
    public int AlreadyDone { get; set; }

    /// <summary>Archivos que siguen sin poder copiarse al terminar (los que otra técnica sí consigue dejan de contar).</summary>
    public int Failed => FailedFiles.Count;

    public long BytesCopied { get; set; }
    public int Reconnects { get; set; }
    public List<string> FailedFiles { get; } = new();

    /// <summary>Técnicas con las que se ha copiado algo, en orden de uso.</summary>
    public List<string> TechniquesUsed { get; } = new();

    /// <summary>Aviso si se leyó de una copia de seguridad (puede estar desactualizada).</summary>
    public string? SnapshotWarning { get; set; }

    /// <summary>
    /// Se pone al terminar el primer escaneo. "Ya copiados" se refiere a lo que había ANTES de esta ejecución:
    /// tras una reconexión no se vuelve a calcular, o se contaría como "ya hecho" lo que acabamos de copiar.
    /// </summary>
    public bool BaselineSet { get; set; }
}

public enum TransferPhase { Scanning, Downloading, Verifying, Waiting, Done, Organizing }

public sealed record TransferProgress(
    TransferPhase Phase,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    string? CurrentFile,
    long CurrentBytes,
    long CurrentSize,
    double BytesPerSecond);
