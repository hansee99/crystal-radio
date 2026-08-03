using System.Windows;
using System.Windows.Controls;
using RadioPlayer.Controls;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The options dialog is four rail sections in a fixed frame, and every one of its parts is a
/// <c>StaticResource</c> lookup into Theme.xaml. A renamed or deleted key there doesn't fail the
/// build — it throws when someone opens the dialog. These pin the keys the layout depends on, plus
/// the display-name rule the DJ voice dropdown needs.
/// </summary>
public class OptionsDialogRailTests
{
    static OptionsDialogRailTests()
    {
        // Same bootstrap as StatePanelStyleTests: outside a running Application nothing has
        // registered the "pack" URI scheme or told it which assembly to read resources from.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = Application.Current;
        Application.ResourceAssembly ??= typeof(StatePanel).Assembly;
    }

    private static ResourceDictionary LoadTheme() => new()
    {
        Source = new Uri("pack://application:,,,/crystal-radio;component/Views/Theme.xaml", UriKind.Absolute)
    };

    // --- The keys the dialog's XAML asks for --------------------------------------------------

    [Theory]
    [InlineData("DialogRailItem", typeof(ListBoxItem))]
    [InlineData("DialogChip", typeof(Border))]
    [InlineData("DialogChipText", typeof(TextBlock))]
    [InlineData("DialogNoteBlock", typeof(Border))]
    [InlineData("DialogNoteText", typeof(TextBlock))]
    // Pre-existing styles the redesign reuses rather than reinventing.
    [InlineData("DialogSectionHeader", typeof(TextBlock))]
    [InlineData("DialogFieldLabel", typeof(TextBlock))]
    [InlineData("DialogHint", typeof(TextBlock))]
    [InlineData("DialogTextBox", typeof(TextBox))]
    [InlineData("DialogComboBox", typeof(ComboBox))]
    [InlineData("DialogPrimaryButton", typeof(Button))]
    [InlineData("DialogGhostButton", typeof(Button))]
    public void ThemeCarriesTheStyleForItsTargetType(string key, Type targetType)
    {
        var style = LoadTheme()[key] as Style;

        Assert.NotNull(style);
        Assert.Equal(targetType, style!.TargetType);
    }

    [Theory]
    [InlineData("TealTint07")]
    [InlineData("TealBorder22")]
    [InlineData("TealBorder35")]
    public void ThemeCarriesTheRailAndNoteBrushes(string key)
    {
        Assert.NotNull(LoadTheme()[key] as System.Windows.Media.Brush);
    }

    /// <summary>A rail item has to be templated end to end. Fall back to the default ListBoxItem
    /// template and Windows' system selection highlight shows up mid-dialog — the exact class of
    /// bug the shared dialog chrome exists to prevent.</summary>
    [Fact]
    public void TheRailItemIsFullyTemplated()
    {
        var style = (Style)LoadTheme()["DialogRailItem"];

        Assert.Contains(style.Setters.OfType<Setter>(),
            s => s.Property == Control.TemplateProperty);
    }

    /// <summary>
    /// The combo's closed box renders inside a ToggleButton in its template, and ToggleButton's own
    /// default style sets Foreground. Because <c>Control.Foreground</c> IS
    /// <c>TextElement.Foreground</c>, that setter cuts the inheritance chain from the ComboBox — so
    /// the selected voice rendered in the system's pure black on a near-black fill. Shipped that
    /// way and was only caught by looking at a render (measured #FF000000 before, TextBody after).
    /// </summary>
    [Fact]
    public void TheComboForwardsItsForegroundToTheClosedBox()
    {
        var forwarded = OnStaThread(() =>
        {
            var style = (Style)LoadTheme()["DialogComboBox"];
            var template = (ControlTemplate)style.Setters.OfType<Setter>()
                .Single(s => s.Property == Control.TemplateProperty).Value;

            var toggle = Flatten((DependencyObject)template.LoadContent())
                .OfType<System.Windows.Controls.Primitives.ToggleButton>()
                .Single();

            // A local value beats ToggleButton's style setter; UnsetValue means the black is back.
            return toggle.ReadLocalValue(Control.ForegroundProperty);
        });

        Assert.NotEqual(DependencyProperty.UnsetValue, forwarded);
    }

    private static IEnumerable<DependencyObject> Flatten(DependencyObject root)
    {
        yield return root;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root)
                     .OfType<DependencyObject>())
            foreach (var d in Flatten(child))
                yield return d;
    }

    /// <summary>Creating WPF visuals needs an STA thread; xUnit runs tests on MTA ones.</summary>
    private static T OnStaThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new System.Threading.Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException($"STA thread threw: {failure}");
        return result;
    }

    // --- The DJ voice display rule -------------------------------------------------------------

    /// <summary>The dropdown used to show the enum's member names, so one of five read "LateNight".
    /// Split by rule rather than a table, so a personality added to the enum reads correctly
    /// without anyone remembering to update a second list.</summary>
    [Theory]
    [InlineData(DjPersonality.Warm, "Warm")]
    [InlineData(DjPersonality.Upbeat, "Upbeat")]
    [InlineData(DjPersonality.Wry, "Wry")]
    [InlineData(DjPersonality.Professional, "Professional")]
    [InlineData(DjPersonality.LateNight, "Late night")]
    public void DisplayNameReadsAsASentence(DjPersonality value, string expected)
    {
        Assert.Equal(expected, OptionsDialog.DisplayName(value));
    }

    /// <summary>Every voice must have a display name, and none may leak a camel-cased member name
    /// into the UI — including any added after this was written.</summary>
    [Fact]
    public void EveryVoiceHasAReadableName()
    {
        foreach (var value in Enum.GetValues<DjPersonality>())
        {
            var label = OptionsDialog.DisplayName(value);

            Assert.False(string.IsNullOrWhiteSpace(label));
            // No interior capital: "LateNight" would fail, "Late night" passes.
            Assert.DoesNotContain(label.Skip(1), char.IsUpper);
        }
    }

    /// <summary>The stored value is the enum's own name, NOT the display label — settings.json has
    /// to keep saying "LateNight" or ResolveDjPersonality can't parse it back.</summary>
    [Fact]
    public void TheStoredNameIsUnaffectedByTheDisplayName()
    {
        foreach (var value in Enum.GetValues<DjPersonality>())
        {
            var stored = new AppSettings { DjPersonality = value.ToString() };

            Assert.Equal(value, stored.ResolveDjPersonality());
        }
    }
}
