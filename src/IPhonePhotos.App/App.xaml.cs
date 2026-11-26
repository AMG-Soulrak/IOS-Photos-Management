using System.IO;
using System.Windows;
using System.Windows.Threading;
using IPhonePhotos.Core;

namespace IPhonePhotos.App;

public partial class App : Application
{
    /// <summary>Opciones de arranque (útiles para pruebas): --source-folder &lt;carpeta&gt; y --dest &lt;carpeta&gt;.</summary>
    public static string? SourceFolderArg { get; private set; }
    public static string? DestArg { get; private set; }
    public static string? BackupFolderArg { get; private set; }

    /// <summary>Técnicas de conexión: todas (el selector usa la mejor) o una carpeta local con --source-folder.</summary>
    public static List<IPhonePhotos.Core.Backends.IDeviceBackendProvider> CreateProviders(bool includeBackup)
    {
        var providers = new List<IPhonePhotos.Core.Backends.IDeviceBackendProvider>();
        if (!string.IsNullOrEmpty(SourceFolderArg)) { providers.Add(new IPhonePhotos.Core.Backends.LocalFolderProvider(SourceFolderArg)); return providers; }
        providers.Add(new IPhonePhotos.Core.Backends.WpdProvider());
        providers.Add(new IPhonePhotos.Core.Backends.AfcProvider());
        if (includeBackup)
            providers.Add(new IPhonePhotos.Core.Backends.BackupProvider(string.IsNullOrEmpty(BackupFolderArg) ? null : new[] { BackupFolderArg }));
        return providers;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--source-folder") SourceFolderArg = e.Args[i + 1];
            if (e.Args[i] == "--dest") DestArg = e.Args[i + 1];
            if (e.Args[i] == "--backup-folder") BackupFolderArg = e.Args[i + 1];
        }

        DispatcherUnhandledException += OnUnhandled;
        Theme.Init();
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
            var path = Path.Combine(Path.GetDirectoryName(exe)!, "iphotos-error.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{e.Exception}\n\n");
        }
        catch { /* sin permiso de escritura: se muestra igualmente */ }

        MessageBox.Show(
            "Ha ocurrido un error inesperado:\n\n" + e.Exception.Message +
            "\n\nLo ya copiado está a salvo: puedes volver a abrir la aplicación y pulsar Descargar para continuar.",
            "IOS Photos", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
