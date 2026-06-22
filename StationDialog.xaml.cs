using System.Windows;
using RadioPlayer.Models;

namespace RadioPlayer;

/// <summary>Modal editor for a single station (name, stream URL, format).</summary>
public partial class StationDialog : Window
{
    public Station? Result { get; private set; }

    public StationDialog(Station? existing)
    {
        InitializeComponent();

        FormatBox.ItemsSource = Enum.GetValues<StreamFormat>();

        if (existing is not null)
        {
            Title = "Edit station";
            NameBox.Text = existing.Name;
            UrlBox.Text = existing.Url;
            FormatBox.SelectedItem = existing.Format;
        }
        else
        {
            Title = "Add station";
            FormatBox.SelectedItem = StreamFormat.Aac;
        }

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var url = UrlBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Please enter a name.");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowError("Please enter a valid http:// or https:// stream URL.");
            return;
        }

        Result = new Station(name, url, (StreamFormat)FormatBox.SelectedItem!);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
