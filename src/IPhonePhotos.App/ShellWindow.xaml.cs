using System.IO;
using System.Windows;
using IPhonePhotos.Core;

namespace IPhonePhotos.App;

/// <summary>
/// Ventana principal: el álbum de fotos, con una barra lateral como la de Dispositivos Apple. Bajar las fotos del iPhone
/// es una opción más, que abre la ventana de descarga (la app nunca borra nada del iPhone).
/// </summary>
public partial class ShellWindow : Window
{
    private readonly ActivityLog _log = new();
    private readonly AlbumView _album;
    private readonly DuplicatesView _dups;
    private readonly PeopleView _people;
    private readonly DiscardView _discard;
    private string _folder = "";

    public ShellWindow()
    {
        InitializeComponent();
        _album = new AlbumView(_log);
        _dups = new DuplicatesView(_log);
        _dups.FilesMoved += () => _ = ReloadAsync();
        _people = new PeopleView(_log);
        _discard = new DiscardView(_log);
        _album.DataLoaded += OnAlbumLoaded;
        AlbumsHost.Content = _album.AlbumsPanel;
        _album.AlbumPicked += () => NavAlbum.IsChecked = true; // elegir un álbum del submenú muestra la página Álbum
        _discard.FilesMoved += () => _ = ReloadAsync();
        Host.Content = _album;
        _folder = LibraryFolder();
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>La carpeta donde la app guarda las fotos: la misma que usa la descarga (si no, «Imágenes\iPhone»).</summary>
    private static string LibraryFolder()
    {
        var path = App.DestArg ?? PortableSettings.Load().Destination
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iPhone");
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    private async Task ReloadAsync()
    {
        FolderText.Text = _folder;
        FolderText.ToolTip = _folder;
        await _album.LoadAsync(_folder);
    }

    // Cada vez que el álbum relee la carpeta (también tras mover o descartar desde él), las otras páginas reciben los datos nuevos.
    private void OnAlbumLoaded()
    {
        _dups.SetLibrary(_folder, _album.Data);
        _people.SetLibrary(_folder, _album.Data);
        _discard.SetLibrary(_folder, _album.Data);
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        Host.Content = NavDups.IsChecked == true ? _dups : NavPeople.IsChecked == true ? _people : NavDiscard.IsChecked == true ? _discard : _album;
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta con las fotos" };
        if (Directory.Exists(_folder)) dialog.InitialDirectory = _folder;
        if (dialog.ShowDialog(this) != true) return;
        _folder = dialog.FolderName;
        var settings = PortableSettings.Load();
        settings.Destination = _folder; // la descarga del iPhone guardará en la misma carpeta
        settings.Save();
        _ = ReloadAsync();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) => Theme.Toggle();

    private MainWindow? _download;

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        // Una sola ventana de descarga: dos a la vez podían escribir en el mismo destino y el mismo manifiesto.
        if (_download != null) { _download.Activate(); return; }
        _download = new MainWindow { Owner = this };
        _download.Closed += (_, _) => { _download = null; _folder = LibraryFolder(); _ = ReloadAsync(); }; // puede haber fotos nuevas o otra carpeta
        _download.Show();
    }

    private void OnOrganizeDate(object sender, RoutedEventArgs e) => Organize(OrganizeMode.ByDate);
    private void OnOrganizePlace(object sender, RoutedEventArgs e) => Organize(OrganizeMode.ByPlace);
    private void OnLivePhotos(object sender, RoutedEventArgs e) => Organize(OrganizeMode.LivePhotos);

    private void Organize(OrganizeMode mode)
    {
        new OrganizeWindow(_log, _folder, mode) { Owner = this }.ShowDialog();
        _ = ReloadAsync(); // las fotos pueden haber cambiado de carpeta
    }
}
