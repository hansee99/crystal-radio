using System.Windows;
using System.Windows.Controls;
using RadioPlayer.Controls;
using RadioPlayer.ViewModels;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Guards the one <see cref="StatePanel"/> failure mode that is silent at runtime: if Theme.xaml
/// stops carrying an implicit style for the control, WPF falls back to ContentControl's default
/// template — which renders only Content, so every Empty/Loading/Error message in the app would
/// quietly stop appearing while the lists kept working. No exception, no visual clue.
/// </summary>
public class StatePanelStyleTests
{
    static StatePanelStyleTests()
    {
        // Outside a running WPF Application nothing has registered the "pack" URI scheme or its
        // WebRequest factory. Touching PackUriHelper registers the scheme; touching Application
        // runs the type initializer that registers the factory; ResourceAssembly tells
        // "pack://application:,,," which assembly to read resources from.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = Application.Current;
        Application.ResourceAssembly ??= typeof(StatePanel).Assembly;
    }

    private static ResourceDictionary LoadTheme() => new()
    {
        Source = new Uri("pack://application:,,,/crystal-radio;component/Views/Theme.xaml", UriKind.Absolute)
    };

    [Fact]
    public void Theme_DefinesImplicitStyleWithATemplate()
    {
        var style = LoadTheme()[typeof(StatePanel)] as Style;

        Assert.NotNull(style);
        Assert.Equal(typeof(StatePanel), style!.TargetType);
        Assert.Contains(style.Setters.OfType<Setter>(), s => s.Property == Control.TemplateProperty);
    }

    [Theory]
    [InlineData("PanelMessage")]
    [InlineData("PanelErrorMessage")]
    [InlineData("PanelProgress")]        // staged checklist + skeletons + cancel
    [InlineData("ProgressStageRow")]
    public void Theme_DefinesTheSharedPanelTemplates(string key) =>
        Assert.IsType<DataTemplate>(LoadTheme()[key]);

    /// <summary>Every state value needs a trigger; a missing one would leave that state
    /// rendering nothing at all.</summary>
    [Fact]
    public void Template_HasATriggerForEveryPanelState()
    {
        var style = (Style)LoadTheme()[typeof(StatePanel)]!;
        var template = (ControlTemplate)style.Setters.OfType<Setter>()
            .First(s => s.Property == Control.TemplateProperty).Value;

        var covered = template.Triggers.OfType<Trigger>()
            .Where(t => t.Property == StatePanel.StateProperty)
            .Select(t => t.Value)
            .Distinct()
            .ToList();

        foreach (var state in Enum.GetValues<PanelState>())
            Assert.Contains(state, covered);
    }
}
