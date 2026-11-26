using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace IPhonePhotos.App;

/// <summary>Una foto para la vista grande. Solo se lee: la vista no toca ni modifica archivos.</summary>
public sealed record PreviewPhoto(string Path, string Name, string Info, string? Extra = null);

/// <summary>
/// Botón «ampliar» de las miniaturas y apertura de la vista grande, igual en todas las vistas (álbum, repetidas, personas,
/// descartes). El botón aparece al pasar el ratón o con el foco de teclado en la miniatura; doble clic, Intro y Espacio también abren
/// (en el álbum, Espacio sigue marcando, como Ctrl+clic). El clic normal en la miniatura sigue siendo suyo (seleccionar, marcar…).
/// </summary>
public static class Preview
{
    public static void Open(DependencyObject from, IReadOnlyList<PreviewPhoto> photos, int index, Action<int>? pick = null, Func<int, bool>? isPicked = null)
    {
        if (photos.Count == 0) return;
        new PhotoPreviewWindow(photos, index, pick, isPicked) { Owner = Window.GetWindow(from) }.ShowDialog();
    }

    /// <summary>¿El clic salió de un botón (el de ampliar)? Entonces no debe seleccionar ni arrastrar la miniatura.</summary>
    public static bool FromButton(object? originalSource)
    {
        for (var d = originalSource as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    /// <summary>Crea el botón de ampliar (para vistas hechas en código) y lo muestra con el ratón encima o con el foco en <paramref name="host"/>.</summary>
    public static Button ExpandButton(FrameworkElement host, string fileName, Action open)
    {
        var button = new Button { Style = (Style)Application.Current.FindResource("ExpandThumbButton") };
        AutomationProperties.SetName(button, "Ampliar foto " + fileName);
        button.Click += (_, e) => { e.Handled = true; open(); };
        void Update() => button.Opacity = host.IsMouseOver || host.IsKeyboardFocusWithin ? 1 : 0;
        host.MouseEnter += (_, _) => Update();
        host.MouseLeave += (_, _) => Update();
        host.IsKeyboardFocusWithinChanged += (_, _) => Update();
        return button;
    }

    /// <summary>Miniatura hecha en código: enfocable con anillo visible, nombre para el lector, doble clic / Intro / Espacio abren.</summary>
    public static void MakeOpenable(FrameworkElement thumb, string fileName, Action open, bool spaceOpens = true)
    {
        thumb.Focusable = true;
        thumb.FocusVisualStyle = (Style)Application.Current.FindResource("FocusRing");
        if (string.IsNullOrEmpty(AutomationProperties.GetName(thumb))) AutomationProperties.SetName(thumb, fileName);
        AutomationProperties.SetHelpText(thumb, "Intro o doble clic: ver en grande");
        thumb.PreviewMouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && !FromButton(e.OriginalSource)) { e.Handled = true; open(); } };
        thumb.KeyDown += (_, e) =>
        {
            if (e.OriginalSource != thumb) return; // la tecla era para un control de dentro (p. ej. la casilla de descartes)
            if (e.Key == Key.Enter || (spaceOpens && e.Key == Key.Space)) { e.Handled = true; open(); }
        };
    }
}
