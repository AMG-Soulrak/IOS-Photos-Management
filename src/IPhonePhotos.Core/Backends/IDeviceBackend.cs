using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Backends;

/// <summary>
/// Una "técnica" para hablar con el dispositivo. Descubre dispositivos y abre una conexión.
/// Hoy hay WPD/MTP y carpeta local (pruebas); más adelante se pueden añadir otras (AFC, copia de seguridad…)
/// sin tocar el resto de la aplicación.
/// </summary>
public interface IDeviceBackendProvider
{
    /// <summary>Nombre legible de la técnica.</summary>
    string Technique { get; }

    /// <summary>Clave corta para elegir técnica a mano: wpd, afc, backup…</summary>
    string Key { get; }

    /// <summary>Preferencia base entre técnicas en vivo (mayor = antes). Se afina probando cada una.</summary>
    int Priority { get; }

    /// <summary>
    /// Es una foto fija de otro momento (copia de seguridad). Nunca se elige sola si el iPhone está conectado
    /// y alguna técnica en vivo funciona.
    /// </summary>
    bool IsSnapshot { get; }

    /// <summary>Dispositivos visibles ahora mismo con esta técnica.</summary>
    IReadOnlyList<DeviceDescriptor> Discover();

    /// <summary>Abre una conexión de solo lectura: desde v1.5 la aplicación nunca escribe ni borra en el dispositivo (ADR-0001).</summary>
    IDeviceBackend Open(DeviceDescriptor device);
}

/// <summary>Conexión abierta con un dispositivo. Solo lectura: no hay ninguna operación que escriba en él.</summary>
public interface IDeviceBackend : IDisposable
{
    DeviceDescriptor Device { get; }
    BackendCapabilities Capabilities { get; }

    /// <summary>Recorre las carpetas de fotos del dispositivo (perezosamente, para poder cancelar).</summary>
    IEnumerable<RemoteFile> EnumerateMedia(CancellationToken ct);

    /// <summary>Copia el contenido del archivo al stream de destino. El stream aporta hash y progreso.</summary>
    Task CopyToAsync(RemoteFile file, Stream destination, CancellationToken ct);

    /// <summary>¿Sigue conectado el dispositivo? Se usa para distinguir "error del archivo" de "cable desenchufado".</summary>
    bool IsAlive();
}
