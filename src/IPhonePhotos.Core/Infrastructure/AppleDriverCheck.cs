using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;

namespace IPhonePhotos.Core;

public sealed record DriverStatus(bool Installed, bool Running, string Message);

/// <summary>
/// Comprueba si el servicio de Apple (usbmuxd) está disponible. Es la única dependencia que NO se puede hacer
/// portable: la instala Apple Devices o iTunes. Es solo un aviso: si no se detecta, la app igualmente intenta conectar.
///
/// Hay dos variantes y se aceptan ambas:
///  · iTunes: un servicio de Windows llamado "Apple Mobile Device Service".
///  · Apple Devices (Microsoft Store): NO es un servicio de Windows; es un proceso normal (AppleMobileDeviceProcess)
///    que escucha en 127.0.0.1:27015, igual que el servicio clásico.
/// </summary>
public static class AppleDriverCheck
{
    /// <summary>¿Responde usbmuxd (127.0.0.1:27015)? Tarda milisegundos; sin él, libimobiledevice tarda ~5 s en rendirse.</summary>
    public static bool UsbmuxdReachable()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, 27015).Wait(400) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static DriverStatus Check()
    {
        // 1) Variante clásica (iTunes): servicio de Windows.
        try
        {
            using var sc = new ServiceController("Apple Mobile Device Service");
            var status = sc.Status; // lanza InvalidOperationException si el servicio no existe
            if (status == ServiceControllerStatus.Running)
                return new DriverStatus(true, true, "Servicio de Apple activo.");
            return new DriverStatus(true, false,
                $"El servicio de Apple está en estado '{status}'. Inícialo (services.msc) o reinstala Apple Devices.");
        }
        catch (InvalidOperationException)
        {
            // No es la variante clásica: se sigue con la de la Microsoft Store.
        }
        catch (Exception ex)
        {
            return new DriverStatus(false, false, $"No se pudo comprobar el servicio de Apple: {ex.Message}");
        }

        // 2) Apple Devices (Microsoft Store): proceso normal que escucha en el puerto de usbmuxd.
        if (UsbmuxdReachable())
            return new DriverStatus(true, true, "Servicio de Apple activo (Apple Devices).");

        var running = Process.GetProcessesByName("AppleMobileDeviceProcess").Length > 0
                      || Process.GetProcessesByName("AppleMobileDeviceService").Length > 0;
        if (running)
            return new DriverStatus(true, false,
                "El servicio de Apple se está iniciando. Espera unos segundos y pulsa «Buscar de nuevo».");

        return new DriverStatus(false, false,
            "No se detecta el servicio de Apple. Instala «Apple Devices» (Microsoft Store) o iTunes; " +
            "si ya está instalado, ábrelo una vez para que arranque el servicio.");
    }
}
