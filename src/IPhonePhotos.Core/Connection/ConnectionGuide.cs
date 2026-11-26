namespace IPhonePhotos.Core.Connection;

/// <summary>Qué puede hacer el usuario desde una fila de la guía (la ventana pone el botón).</summary>
public enum GuideAction { None, Retry, OpenStore, Diagnostic }

/// <summary>Una comprobación de la lista «Antes de conectar». <paramref name="DetectedByApp"/>: la app sabe su estado real.</summary>
public sealed record GuideCheck(string Id, string Title, string Text, bool DetectedByApp = false);

/// <summary>Una fila de «Solución de problemas»: síntoma → causa → qué hacer.</summary>
public sealed record GuideRow(string Id, string Symptom, string Cause, string Action, GuideAction Button = GuideAction.Retry);

/// <summary>
/// ÚNICA fuente de los textos de conexión: la usan la guía «Conectar tu iPhone o iPad», el asistente y los mensajes de error.
/// Nada de lo que dice está probado con un iPhone real todavía (ver TODO.md); las rutas de Ajustes de iOS se citan avisando
/// de que su nombre cambia según la versión.
/// </summary>
public static class ConnectionGuide
{
    public const string IosNamesVary = "Según tu versión de iOS, los nombres pueden variar.";

    public const string StoreSearchUri = "ms-windows-store://search/?query=Apple%20Devices";

    public static readonly IReadOnlyList<GuideCheck> Checklist = new[]
    {
        new GuideCheck("cable", "Cable de datos directo al PC",
            "Usa un cable que transmita datos (no solo carga), enchufado directamente al PC, sin hub ni base. Si no va, prueba otro cable u otro puerto USB."),
        new GuideCheck("desbloqueado", "iPhone o iPad desbloqueado",
            "Desbloquéalo y deja la pantalla encendida durante toda la copia. Si se bloquea, la copia se corta."),
        new GuideCheck("confiar", "«Confiar en este ordenador»",
            "La primera vez, el teléfono pregunta si confías en este ordenador: pulsa «Confiar» e introduce el código del teléfono."),
        new GuideCheck("controlador", "Controlador de Apple instalado y en marcha",
            "Hace falta «Apple Devices» (Microsoft Store) o iTunes. La app comprueba si el servicio está activo.", DetectedByApp: true),
        new GuideCheck("originales", "Conservar originales",
            "En el teléfono: Ajustes → Fotos → «Transferir a Mac o PC» → «Conservar originales». Si faltan fotos, desactiva un tiempo «Optimizar almacenamiento» para que se descarguen al teléfono. " + IosNamesVary),
        new GuideCheck("sin-inalambrico", "Siempre con cable",
            "La copia necesita el cable USB. El Wi-Fi o el modo de ahorro de batería no sustituyen al cable."),
    };

    public static readonly IReadOnlyList<GuideRow> Troubles = new[]
    {
        new GuideRow("pc-no-ve", "El PC no ve el iPhone",
            "El cable no transmite datos o el puerto no responde.",
            "Conecta el cable directamente al PC (sin hub), prueba otro cable u otro puerto y pulsa «Reintentar»."),
        new GuideRow("bloqueado", "El iPhone aparece, pero no se ven fotos",
            "El iPhone está bloqueado o todavía no has pulsado «Confiar».",
            "Desbloquea el iPhone y pulsa «Confiar». La app vuelve a intentarlo sola cada pocos segundos."),
        new GuideRow("confiar", "Sale el aviso de «Confiar» en el iPhone",
            "El iPhone espera que aceptes este ordenador.",
            "Pulsa «Confiar» en el iPhone e introduce el código del teléfono."),
        new GuideRow("no-confiar", "Salió el aviso de Confiar y pulsé «No confiar»",
            "El iPhone recuerda que no confías en este ordenador y ya no vuelve a preguntar.",
            "En el iPhone: Ajustes → General → Transferir o restablecer → Restablecer → «Restablecer ubicación y privacidad». Luego desconecta y vuelve a conectar el cable y pulsa «Confiar». " + IosNamesVary),
        new GuideRow("faltan-fotos", "Faltan fotos o salen borrosas",
            "Con «Optimizar almacenamiento» de iCloud, parte de las fotos solo está en iCloud, no en el teléfono.",
            "En el iPhone: Ajustes → Fotos → «Descargar y conservar originales» y «Transferir a Mac o PC» → «Conservar originales». Espera a que se descarguen y copia de nuevo. " + IosNamesVary,
            GuideAction.None),
        new GuideRow("copia-se-corta", "La copia se corta",
            "El cable se movió o el iPhone se bloqueó a mitad de la copia.",
            "Revisa el cable y pon el bloqueo automático en «Nunca» durante la copia (Ajustes → Pantalla y brillo → Bloqueo automático). Pulsa «Continuar»: lo copiado no se repite. " + IosNamesVary),
        new GuideRow("sin-apple-devices", "Apple Devices o iTunes no está instalado",
            "Falta el controlador de Apple que Windows necesita para hablar con el iPhone.",
            "Instala «Apple Devices» desde Microsoft Store (o iTunes), ábrelo una vez y pulsa «Reintentar».",
            GuideAction.OpenStore),
        new GuideRow("antivirus", "El antivirus o la seguridad lo bloquea",
            "Un programa de seguridad impide que la app use el controlador de Apple.",
            "Permite IOS Photos en tu antivirus o programa de seguridad y pulsa «Reintentar»."),
        new GuideRow("sigue-sin-funcionar", "Sigue sin funcionar",
            "La causa no está clara.",
            "Pulsa «Diagnóstico»: guarda un informe sin el nombre ni el número de serie del teléfono que puedes enviar a quien te ayude.",
            GuideAction.Diagnostic),
    };

    public static GuideRow Row(string id) => Troubles.First(r => r.Id == id);

    /// <summary>Fila de la guía que explica cada problema de conexión.</summary>
    public static string RowIdFor(ConnectionProblem problem) => problem switch
    {
        ConnectionProblem.DriverMissing => "sin-apple-devices",
        ConnectionProblem.NotVisible => "pc-no-ve",
        ConnectionProblem.Locked or ConnectionProblem.NoPhotosVisible => "bloqueado",
        ConnectionProblem.TrustPending => "confiar",
        ConnectionProblem.TrustDenied => "no-confiar",
        ConnectionProblem.Disconnected => "copia-se-corta",
        _ => "sigue-sin-funcionar"
    };
}
