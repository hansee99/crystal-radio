using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RadioPlayer.Mvvm;

/// <summary>
/// MultiBinding converter for the per-row now-playing indicator.
/// values[0] = Station.Url (string), values[1] = MainViewModel.NowPlayingUrl (string?)
/// Returns Visible when both are equal (non-empty), Collapsed otherwise.
/// Pass ConverterParameter="Invert" to flip — used to hide the icon when the equalizer shows.
/// </summary>
public sealed class UrlIsPlayingMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var url       = values.Length > 0 ? values[0] as string : null;
        var nowPlaying = values.Length > 1 ? values[1] as string : null;
        bool isMatch  = !string.IsNullOrEmpty(url) && url == nowPlaying;

        bool invert = parameter is "Invert";
        bool visible = invert ? !isMatch : isMatch;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
