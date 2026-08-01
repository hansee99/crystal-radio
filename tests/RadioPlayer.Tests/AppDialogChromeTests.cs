using System.Threading;
using System.Windows;
using RadioPlayer.Controls;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Guards the wiring that makes <see cref="AppDialog"/>'s chrome actually appear. WPF matches
/// implicit styles on an element's EXACT runtime type and does not walk base classes, so a
/// <c>TargetType="AppDialog"</c> style in Theme.xaml never reaches AboutDialog/OptionsDialog/
/// StationDialog on its own — <see cref="AppDialog"/>'s constructor has to ask for it explicitly.
/// This shipped broken once: the template silently never applied and, because the window is
/// transparent by design, the dialogs rendered as unreadable floating text over the main window.
/// </summary>
public class AppDialogChromeTests
{
    [Fact]
    public void Constructor_RequestsTheChromeStyle()
    {
        var localStyleValue = OnStaThread(() =>
        {
            var dialog = new AppDialog();
            return dialog.ReadLocalValue(FrameworkElement.StyleProperty);
        });

        // A resource reference, not UnsetValue: something is asking for the chrome style. If the
        // SetResourceReference call is ever dropped in favour of "the implicit style will do it",
        // this goes back to UnsetValue and the dialogs lose their surface again.
        Assert.NotEqual(DependencyProperty.UnsetValue, localStyleValue);
    }

    [Fact]
    public void Constructor_SetsUpTheTransparentBorderlessFrame()
    {
        var (style, allowsTransparency, resizeMode) = OnStaThread(() =>
        {
            var dialog = new AppDialog();
            return (dialog.WindowStyle, dialog.AllowsTransparency, dialog.ResizeMode);
        });

        // The template draws the surface, so the window itself must be borderless + transparent.
        Assert.Equal(WindowStyle.None, style);
        Assert.True(allowsTransparency);
        Assert.Equal(ResizeMode.NoResize, resizeMode);
    }

    /// <summary>Creating any Window requires an STA thread; xUnit runs tests on MTA ones.</summary>
    private static T OnStaThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException($"STA thread threw: {failure}");
        return result;
    }
}
