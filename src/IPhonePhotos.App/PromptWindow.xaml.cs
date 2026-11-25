using System.Windows;

namespace IPhonePhotos.App;

/// <summary>Pide un nombre (WPF no trae un cuadro de texto simple como «InputBox»). Sirve para crear y para renombrar álbumes.</summary>
public partial class PromptWindow : Window
{
    public string? Result { get; private set; }

    public PromptWindow(string title = "Nuevo álbum", string initial = "", string okText = "Crear")
    {
        InitializeComponent();
        Title = title;
        OkButton.Content = okText;
        Box.Text = initial;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Box.Text)) { Hint.Text = "Escribe un nombre para el álbum."; return; }
        Result = Box.Text.Trim();
        DialogResult = true;
    }
}
