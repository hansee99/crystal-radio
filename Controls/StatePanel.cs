using System.Windows;
using System.Windows.Controls;
using RadioPlayer.ViewModels;

namespace RadioPlayer.Controls;

/// <summary>
/// A list panel with four mutually-exclusive presentations — content, empty, loading, error —
/// selected by <see cref="State"/> (UX audit). The normal <see cref="ContentControl.Content"/>
/// holds the list itself; the three alternates are separate slots so a panel can't render two
/// states at once, which is what allowed "describe a vibe above" to sit under a running spinner.
///
/// Each alternate slot has its own Content/ContentTemplate pair, so a panel can pass a plain
/// string plus one of Theme.xaml's shared message templates
/// (<c>PanelMessage</c> / <c>PanelLoadingMessage</c> / <c>PanelErrorMessage</c>) and get the
/// same look everywhere. All four slots live in the template at once; only one is visible, so
/// switching states never rebuilds the list's visual tree.
/// </summary>
public class StatePanel : ContentControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(PanelState), typeof(StatePanel),
            new PropertyMetadata(PanelState.Content));

    /// <summary>Which slot is showing. Defaults to <see cref="PanelState.Content"/>.</summary>
    public PanelState State
    {
        get => (PanelState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public static readonly DependencyProperty EmptyContentProperty =
        DependencyProperty.Register(nameof(EmptyContent), typeof(object), typeof(StatePanel));

    public object? EmptyContent
    {
        get => GetValue(EmptyContentProperty);
        set => SetValue(EmptyContentProperty, value);
    }

    public static readonly DependencyProperty EmptyContentTemplateProperty =
        DependencyProperty.Register(nameof(EmptyContentTemplate), typeof(DataTemplate), typeof(StatePanel));

    public DataTemplate? EmptyContentTemplate
    {
        get => (DataTemplate?)GetValue(EmptyContentTemplateProperty);
        set => SetValue(EmptyContentTemplateProperty, value);
    }

    public static readonly DependencyProperty LoadingContentProperty =
        DependencyProperty.Register(nameof(LoadingContent), typeof(object), typeof(StatePanel));

    public object? LoadingContent
    {
        get => GetValue(LoadingContentProperty);
        set => SetValue(LoadingContentProperty, value);
    }

    public static readonly DependencyProperty LoadingContentTemplateProperty =
        DependencyProperty.Register(nameof(LoadingContentTemplate), typeof(DataTemplate), typeof(StatePanel));

    public DataTemplate? LoadingContentTemplate
    {
        get => (DataTemplate?)GetValue(LoadingContentTemplateProperty);
        set => SetValue(LoadingContentTemplateProperty, value);
    }

    public static readonly DependencyProperty ErrorContentProperty =
        DependencyProperty.Register(nameof(ErrorContent), typeof(object), typeof(StatePanel));

    public object? ErrorContent
    {
        get => GetValue(ErrorContentProperty);
        set => SetValue(ErrorContentProperty, value);
    }

    public static readonly DependencyProperty ErrorContentTemplateProperty =
        DependencyProperty.Register(nameof(ErrorContentTemplate), typeof(DataTemplate), typeof(StatePanel));

    public DataTemplate? ErrorContentTemplate
    {
        get => (DataTemplate?)GetValue(ErrorContentTemplateProperty);
        set => SetValue(ErrorContentTemplateProperty, value);
    }
}
