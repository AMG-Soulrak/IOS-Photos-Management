using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using IPhonePhotos.Core;
using Microsoft.Win32;

namespace IPhonePhotos.App;

/// <summary>
/// Modo claro y oscuro. Los colores de la app son pinceles con nombre en App.xaml; para cambiar de modo se les cambia el color
/// (el mismo pincel lo usan todas las pantallas, así que cambian a la vez). Sin ajuste guardado se usa el modo de Windows.
/// shortcut: las barras de desplazamiento y los cuadros de mensaje de Windows no se oscurecen (son del sistema); la barra de
/// título sí. Para oscurecerlo todo haría falta restilar cada control.
/// </summary>
public static class Theme
{
    private static readonly (string Key, string Light, string Dark)[] Palette =
    {
        ("PageBrush", "#F3F3F3", "#1C1C1C"),
        ("CardBrush", "#FFFFFF", "#2A2A2A"),
        ("FieldBrush", "#FFFFFF", "#333333"),
        ("ControlBrush", "#FBFBFB", "#363636"),
        ("ControlHover", "#F3F3F3", "#404040"),
        ("Stroke", "#E0E0E0", "#4A4A4A"),
        ("NavSelected", "#E6E6E6", "#383838"),
        ("Accent", "#005FB8", "#5AA3F0"),   // oscuro: ≥ 4,5:1 como texto sobre tarjetas y controles
        ("OnAccent", "#FFFFFF", "#000000"), // texto sobre Accent (botón principal): ≥ 4,5:1 en los dos modos
        ("AccentTint", "#EAF2FB", "#1F3A56"),
        ("Danger", "#C42B1C", "#F28B82"),
        ("DangerTint", "#FDE7E9", "#4A2522"),
        ("Success", "#0F7B0F", "#6CCB5F"),
        ("Label", "#000000", "#F2F2F2"),
        ("Muted", "#616161", "#A6A6A6"),
        ("Line", "#E5E5E5", "#3C3C3C"),
    };

    public static bool IsDark { get; private set; }

    /// <summary>Se llama una vez al arrancar, antes de abrir ventanas.</summary>
    public static void Init()
    {
        var saved = PortableSettings.Load().Theme;
        IsDark = saved == "dark" || (saved != "light" && SystemUsesDark());
        Apply();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((w, _) => TitleBar((Window)w)));
    }

    public static void Toggle()
    {
        IsDark = !IsDark;
        var settings = PortableSettings.Load();
        settings.Theme = IsDark ? "dark" : "light";
        settings.Save();
        Apply();
        foreach (Window w in Application.Current.Windows) TitleBar(w);
    }

    private static void Apply()
    {
        foreach (var (key, light, dark) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(IsDark ? dark : light);
            // Pincel nuevo (los de los estilos están congelados y no se pueden cambiar); las pantallas lo toman con DynamicResource.
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }

    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void TitleBar(Window w)
    {
        try
        {
            var handle = new WindowInteropHelper(w).Handle;
            if (handle == IntPtr.Zero) return;
            var on = IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch { /* Windows antiguo sin esa opción: la barra se queda clara */ }
    }
}
