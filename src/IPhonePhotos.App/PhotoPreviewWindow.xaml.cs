using System.IO;
using System.Windows;
using System.Windows.Input;

namespace IPhonePhotos.App;

/// <summary>
/// Una foto de un grupo de repetidas en grande. Con ← y → (o los botones) se pasa a las otras del grupo para compararlas,
/// y desde aquí se elige con cuál quedarse; Esc cierra.
/// </summary>
public partial class PhotoPreviewWindow : Window
{
    private readonly List<DupCard> _cards;
    private readonly Action<DupCard> _pick;
    private int _index;
    private int _version; // evita que una carga lenta pise a una foto más reciente

    public PhotoPreviewWindow(List<DupCard> cards, int index, Action<DupCard> pick)
    {
        InitializeComponent();
        _cards = cards;
        _pick = pick;
        ShowPhoto(Math.Max(0, index));
    }

    private async void ShowPhoto(int index)
    {
        _index = (index + _cards.Count) % _cards.Count;
        var card = _cards[_index];
        var version = ++_version;
        Title = $"Foto {_index + 1} de {_cards.Count}";
        NameText.Text = card.Name;
        InfoText.Text = card.Info;
        ScoreText.Text = card.ScoreText + (card.Recommended ? " · recomendada por la app" : "");
        PickButton.Content = card.Keep ? "Es la que me quedo" : "Quedarme con esta";
        PickButton.IsEnabled = !card.Keep;
        Loading.Text = "Cargando…";
        Loading.Visibility = Visibility.Visible;
        Big.Source = null;
        var image = await Task.Run(() => ThumbLoader.Decode(card.Thumb.Path, 2600));
        if (version != _version) return;
        Big.Source = image;
        if (image == null) Loading.Text = "No se puede mostrar esta foto (¿falta la extensión HEIF de Windows?).";
        Loading.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrev(object sender, RoutedEventArgs e) => ShowPhoto(_index - 1);
    private void OnNext(object sender, RoutedEventArgs e) => ShowPhoto(_index + 1);

    private void OnPick(object sender, RoutedEventArgs e)
    {
        _pick(_cards[_index]);
        ShowPhoto(_index);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left) ShowPhoto(_index - 1);
        else if (e.Key == Key.Right) ShowPhoto(_index + 1);
        else if (e.Key == Key.Escape) Close();
    }
}
