using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RadioPlayer.ViewModels;

namespace RadioPlayer;

/// <summary>
/// Modal AI-search dialog. It only presents what the view model orchestrates
/// (interpret → search → results) and triggers play on the chosen result.
/// </summary>
public partial class AiSearchDialog : Window
{
    public AiSearchDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PromptBox.Focus();
    }

    // The Play button's Command performs the play (and gates enablement); this only closes.
    private void Play_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedSearchResult is not null)
        {
            vm.PlaySelectedSearchResult();
            DialogResult = true;
        }
    }
}
