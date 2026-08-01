using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace RadioPlayer.Controls;

/// <summary>
/// Shared chrome for the app's modal dialogs (UX audit: About, Options and Add/Edit station all
/// shipped with system title bars, system-blue links and light gray OK/Cancel buttons — after
/// eighteen screens of a carefully toned dark UI they landed like a different application, and
/// they're the two places a new user goes first).
///
/// Gives every dialog the app's own borderless surface, a drag region, and — while it's open —
/// a scrim over the owner window so the running app stops competing for attention. Dialogs get
/// this by deriving from <c>AppDialog</c> instead of <c>Window</c>; the look itself comes from
/// the implicit style in Theme.xaml.
/// </summary>
[TemplatePart(Name = TitleBarPart, Type = typeof(UIElement))]
[TemplatePart(Name = CloseButtonPart, Type = typeof(ButtonBase))]
public class AppDialog : Window
{
    private const string TitleBarPart = "PART_TitleBar";
    private const string CloseButtonPart = "PART_CloseButton";

    private UIElement? _titleBar;
    private ButtonBase? _closeButton;

    public AppDialog()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.Transparent;

        // Pull in Theme.xaml's chrome style explicitly. It CANNOT arrive implicitly: WPF matches
        // implicit styles on the element's exact runtime type and does not walk base classes, so
        // a Style with TargetType AppDialog never reaches AboutDialog/OptionsDialog/StationDialog.
        // Without this the template silently never applies and — because the window is
        // transparent by design — the dialogs render as unreadable floating text over the app.
        SetResourceReference(StyleProperty, typeof(AppDialog));
    }

    /// <summary>
    /// Wires the chrome's behaviour to its template parts. Done here rather than with event
    /// attributes in the template because Theme.xaml is a plain ResourceDictionary and can't
    /// carry event handlers without a code-behind class.
    /// </summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_titleBar is not null)
            _titleBar.MouseLeftButtonDown -= OnTitleBarPressed;
        if (_closeButton is not null)
            _closeButton.Click -= OnCloseClicked;

        _titleBar = GetTemplateChild(TitleBarPart) as UIElement;
        _closeButton = GetTemplateChild(CloseButtonPart) as ButtonBase;

        if (_titleBar is not null)
            _titleBar.MouseLeftButtonDown += OnTitleBarPressed;
        if (_closeButton is not null)
            _closeButton.Click += OnCloseClicked;
    }

    private void OnTitleBarPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetOwnerScrim(true);
    }

    protected override void OnClosed(EventArgs e)
    {
        SetOwnerScrim(false);
        base.OnClosed(e);
    }

    /// <summary>
    /// Set on the owner window while a dialog is open, so it can show a scrim. An attached
    /// property rather than <c>Owner.Opacity</c>: the main window is
    /// <c>AllowsTransparency</c>, so lowering its opacity would show the desktop through it
    /// instead of dimming it. A window opts in by binding something to this.
    /// </summary>
    public static readonly DependencyProperty IsDimmedProperty =
        DependencyProperty.RegisterAttached("IsDimmed", typeof(bool), typeof(AppDialog),
            new PropertyMetadata(false));

    public static void SetIsDimmed(DependencyObject element, bool value) =>
        element.SetValue(IsDimmedProperty, value);

    public static bool GetIsDimmed(DependencyObject element) =>
        (bool)element.GetValue(IsDimmedProperty);

    private void SetOwnerScrim(bool dim)
    {
        if (Owner is not null)
            SetIsDimmed(Owner, dim);
    }
}
