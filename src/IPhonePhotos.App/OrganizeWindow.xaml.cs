using System.IO;
using System.Windows;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Organize;
using IPhonePhotos.Core.Transfer;

namespace IPhonePhotos.App;

public enum OrganizeMode { ByDate, LivePhotos, ByPlace }

/// <summary>
/// Ventana de "Organizar por año/mes" y "Limpiar Live Photos". Trabaja sobre CUALQUIER carpeta con fotos ya descargadas
/// (se elige aquí; no hace falta el iPhone ni que la haya creado esta aplicación). Primero una simulación que no toca
/// nada y solo después se puede aplicar.
/// </summary>
public partial class OrganizeWindow : Window
{
    private readonly ActivityLog _log;
    private readonly OrganizeMode _mode;
    private MovePlan? _plan;
    private string _plannedFolder = "";
    private CancellationTokenSource? _cts;

    public OrganizeWindow(ActivityLog log, string folder, OrganizeMode mode)
    {
        InitializeComponent();
        _log = log;
        _mode = mode;
        FolderBox.Text = folder;

        if (mode == OrganizeMode.ByDate)
        {
            Title = "Organizar por año y mes";
            TitleText.Text = "Ordenar en carpetas Año\\Mes";
            IntroText.Text = "Mueve las fotos y vídeos de la carpeta (y sus subcarpetas) a carpetas como 2024\\03 según la fecha de cada archivo. " +
                             "Solo cambia de carpeta: no borra ni modifica nada. Vale para cualquier carpeta que ya tengas descargada.";
        }
        else if (mode == OrganizeMode.ByPlace)
        {
            Title = "Organizar por lugar";
            TitleText.Text = "Ordenar en carpetas Lugar\\Año";
            IntroText.Text = "Mueve las fotos a carpetas como Madrid\\2024 según el GPS de cada foto (los vídeos de Live Photos van con su foto). " +
                             "Las fotos sin GPS van a «Sin ubicación». Solo cambia de carpeta: no borra ni modifica nada.";
        }
        else
        {
            Title = "Limpiar Live Photos";
            TitleText.Text = "Apartar los clips de vídeo de los Live Photos";
            IntroText.Text = $"Cada Live Photo guarda además un clip .MOV de ~3 segundos. Esta limpieza los aparta a la carpeta «{LivePhotoService.BackupFolder}» " +
                             "dentro de la carpeta elegida (no los borra; puedes borrar esa carpeta tú si quieres el espacio). Los vídeos normales no se tocan.";
        }
    }

    private string Folder => FolderBox.Text.Trim();

    private string ReportKind => _mode switch { OrganizeMode.ByDate => "organizar", OrganizeMode.ByPlace => "organizar-lugar", _ => "livephotos" };
    private string ReportTitle => _mode switch { OrganizeMode.ByDate => "Organización por Año\\Mes", OrganizeMode.ByPlace => "Organización por Lugar\\Año", _ => "Limpieza de Live Photos" };

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta con las fotos ya descargadas" };
        if (Directory.Exists(Folder)) dialog.InitialDirectory = Folder;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void OnFolderChanged(object sender, RoutedEventArgs e)
    {
        // Si se cambia la carpeta, el plan calculado ya no vale.
        if (_plan != null && !string.Equals(_plannedFolder, Folder, StringComparison.OrdinalIgnoreCase))
        {
            _plan = null;
            ApplyButton.IsEnabled = false;
            PlanText.Text = "Pulsa «Ver qué se haría». Todavía no se ha tocado nada.";
            NotesText.Text = "";
            ResultText.Text = "";
        }
    }

    private async void OnCalculate(object sender, RoutedEventArgs e)
    {
        var folder = Folder;
        if (!Directory.Exists(folder))
        {
            PlanText.Text = "Esa carpeta no existe. Elige otra con «Cambiar…».";
            return;
        }

        SetBusy(true);
        ResultText.Text = "";
        NotesText.Text = "";
        ApplyButton.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        PlanText.Text = "Revisando la carpeta…";
        try
        {
            _plan = await Task.Run(() =>
            {
                var progress = new Progress<(int Done, int Total)>(p => DetailText.Text = $"Leyendo metadatos: {p.Done} de {p.Total}");
                var plan = _mode switch
                {
                    OrganizeMode.ByDate => new OrganizeService(_log).Plan(folder),
                    OrganizeMode.ByPlace => new OrganizeService(_log).PlanByPlace(folder, progress),
                    _ => new LivePhotoService(_log).Plan(folder)
                };
                FileMover.WriteReport(folder, ReportKind, ReportTitle, plan, null);
                return plan;
            });
            _plannedFolder = folder;

            PlanText.Text = _plan.Items.Count == 0
                ? $"No hay nada que mover ({_plan.AlreadyInPlace} archivos ya están en su sitio)."
                : $"Se moverían {_plan.Items.Count} archivos ({Format.Bytes(_plan.TotalBytes)}). Ya en su sitio: {_plan.AlreadyInPlace}.";
            NotesText.Text = string.Join("\n", _plan.Notes.Take(15)) + (_plan.Notes.Count > 15 ? $"\n… y {_plan.Notes.Count - 15} avisos más (en el informe)." : "");
            ApplyButton.IsEnabled = _plan.Items.Count > 0;
        }
        catch (Exception ex)
        {
            _plan = null;
            PlanText.Text = "Error: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally
        {
            Bar.IsIndeterminate = false;
            Bar.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _plan.Items.Count == 0) return;
        var folder = _plannedFolder;
        var question = _mode != OrganizeMode.LivePhotos
            ? $"Se van a mover {_plan.Items.Count} archivos a carpetas {(_mode == OrganizeMode.ByPlace ? "Lugar\\Año" : "Año\\Mes")} dentro de:\n{folder}\n\n¿Continuar?"
            : $"Se van a apartar {_plan.Items.Count} clips .MOV a «{LivePhotoService.BackupFolder}» dentro de:\n{folder}\n\nNo se borra nada. ¿Continuar?";
        if (MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        using var gate = LibraryGate.TryEnter("organizar");
        if (gate == null) { ResultText.Text = LibraryGate.BusyMessage; return; }
        SetBusy(true);
        _cts = new CancellationTokenSource();
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = false;
        Bar.Value = 0;
        var plan = _plan;
        var token = _cts.Token;
        try
        {
            var progress = new Progress<TransferProgress>(p =>
            {
                Bar.Value = p.FilesTotal > 0 ? p.FilesDone * 100.0 / p.FilesTotal : 0;
                DetailText.Text = $"Moviendo {p.FilesDone} de {p.FilesTotal}";
            });
            var result = await Task.Run(() =>
            {
                // Si la carpeta es de esta aplicación (tiene manifiesto), se mantiene al día; si no, se ignora.
                using var store = FileMover.OpenExistingManifest(folder);
                return _mode != OrganizeMode.LivePhotos
                    ? new OrganizeService(_log).Apply(folder, store, plan, progress, token, ReportKind, ReportTitle)
                    : new LivePhotoService(_log).Apply(folder, store, plan, progress, token);
            });

            ResultText.Text = $"Hecho: {result.Moved} movidos ({Format.Bytes(result.BytesMoved)}), {result.Failed.Count} con error." +
                              (result.Reconciled > 0 ? $" {result.Reconciled} registros corregidos." : "") +
                              (result.ReportPath != null ? $"\nInforme: {result.ReportPath}" : "");
            _plan = null;
            ApplyButton.IsEnabled = false;
        }
        catch (OperationCanceledException)
        {
            ResultText.Text = "Detenido. Lo ya movido queda en su sitio: puedes repetir cuando quieras.";
        }
        catch (Exception ex)
        {
            ResultText.Text = "Error: " + ex.Message;
            _log.Error(ex.Message);
        }
        finally
        {
            DetailText.Text = "";
            Bar.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        CalcButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        FolderBox.IsEnabled = !busy;
        if (busy) ApplyButton.IsEnabled = false;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }
}
