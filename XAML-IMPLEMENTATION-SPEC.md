# Aurora Radio — WPF / XAML Implementation Spec (Direction A)

Implementation guide for porting the **Direction A** redesign (`Radio Player Redesign.dc.html`)
to a .NET WPF desktop app. Everything in the design is native WPF — no third-party
control libraries are required.

> Reference render: HTML mock, 920 × 600 logical window. All sizes below are in
> device-independent units (WPF "px"), which match the mock 1:1.

---

## 0. Tech mapping at a glance

| Design element | WPF technique |
|---|---|
| Frameless window + custom title bar | `WindowChrome` (`WindowStyle="None"`, `AllowsTransparency` not needed) |
| Ambient wave background | `ImageBrush` on a `Border.Background` + overlay `Border`s with gradient brushes |
| Teal accent glow on play button | `LinearGradientBrush` + `DropShadowEffect` |
| Station list | `ListBox` restyled via `ItemContainerStyle` + `ItemTemplate` |
| Search field, chips, pills | `Border` + `TextBox`/`TextBlock` |
| Transport / icon buttons | `Button` with a custom `ControlTemplate` |
| Volume control | `Slider` with a custom `ControlTemplate` |
| Equalizer bars + "LIVE" dot | `Storyboard` in a `Style.Triggers` (Loaded) |
| Icons | `Path` geometries (vector) — see §7 |

Recommended layout: **MVVM**. The XAML below is presentation-only; bind to a
`PlayerViewModel` / `StationViewModel` as noted in §8.

---

## 1. Color & brush resources

Put these in `App.xaml` → `Application.Resources` (or a merged
`Theme.xaml` ResourceDictionary). These are the exact values used in the mock.

```xml
<!-- Surfaces -->
<Color x:Key="BgDeep">#FF050D0C</Color>
<Color x:Key="BgPanel">#FF06110F</Color>
<SolidColorBrush x:Key="WindowBg"      Color="#FF050D0C"/>
<SolidColorBrush x:Key="PanelBg"       Color="#7F040B0A"/>  <!-- left list panel, ~50% -->
<SolidColorBrush x:Key="Hairline"      Color="#14FFFFFF"/>  <!-- 1px borders, white @ ~8% -->
<SolidColorBrush x:Key="HairlineSoft"  Color="#0FFFFFFF"/>

<!-- Fills (white at low alpha) -->
<SolidColorBrush x:Key="FillSubtle"    Color="#0BFFFFFF"/>  <!-- tiles, search bg -->
<SolidColorBrush x:Key="FillTrack"     Color="#21FFFFFF"/>  <!-- slider track -->

<!-- Text -->
<SolidColorBrush x:Key="TextPrimary"   Color="#FFF3F9F7"/>
<SolidColorBrush x:Key="TextBody"      Color="#FFE7F0EE"/>
<SolidColorBrush x:Key="TextSecondary" Color="#FFBCCCC8"/>
<SolidColorBrush x:Key="TextMuted"     Color="#FF7D938E"/>
<SolidColorBrush x:Key="TextFaint"     Color="#FF5F736F"/>

<!-- Teal accent ramp -->
<Color x:Key="TealColor">#FF56C7BA</Color>
<SolidColorBrush x:Key="Teal"          Color="#FF56C7BA"/>
<SolidColorBrush x:Key="TealBright"    Color="#FF62D2C4"/>
<SolidColorBrush x:Key="TealText"      Color="#FFCDEEE7"/>
<SolidColorBrush x:Key="TealTint10"    Color="#1A56C7BA"/>  <!-- active row bg -->
<SolidColorBrush x:Key="TealTint16"    Color="#2956C7BA"/>  <!-- active tab/tile bg -->

<!-- Primary play button gradient -->
<LinearGradientBrush x:Key="PlayBrush" StartPoint="0,0" EndPoint="1,1">
    <GradientStop Color="#FF6FDCCD" Offset="0"/>
    <GradientStop Color="#FF34988D" Offset="1"/>
</LinearGradientBrush>

<!-- Volume fill gradient -->
<LinearGradientBrush x:Key="VolumeFill" StartPoint="0,0" EndPoint="1,0">
    <GradientStop Color="#FF34988D" Offset="0"/>
    <GradientStop Color="#FF62D2C4" Offset="1"/>
</LinearGradientBrush>
```

### Typography
The mock uses **Hanken Grotesk**. Bundle the TTFs under `/Fonts/` and reference with a
`FontFamily` resource (packs the font into the app so it renders on any machine):

```xml
<FontFamily x:Key="UiFont">/YourApp;component/Fonts/#Hanken Grotesk</FontFamily>
```

Set `TextElement.FontFamily="{StaticResource UiFont}"` on the root `Window`.
If you prefer to avoid bundling, **Segoe UI Variable** is an acceptable Windows-native fallback.

Type scale used:
- Track title: **44px**, Weight `Light` (300), letter-spacing -0.01em (`TextOptions`/no native tracking — leave default)
- Artist: **20px**, Light
- Section/labels (`NOW PLAYING`): **11px**, SemiBold, letter-spacing wide → use a styled run; WPF has no letter-spacing, so insert thin spaces or accept default tracking
- List title: **13.5px** Medium/SemiBold; subtitle **11.5px**
- Tabs / chip text: **12.5–13px**

> Note: WPF has no CSS `letter-spacing`. For the wide-tracked caps labels, either
> accept default spacing or add hair/thin spaces between letters. Don't over-engineer this.

---

## 2. Window shell & custom title bar

```xml
<Window x:Class="AuroraRadio.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="920" Height="600"
        MinWidth="760" MinHeight="520"
        WindowStartupLocation="CenterScreen"
        Background="{StaticResource WindowBg}"
        TextElement.FontFamily="{StaticResource UiFont}"
        TextElement.Foreground="{StaticResource TextBody}"
        WindowStyle="None" ResizeMode="CanResize">

  <WindowChrome.WindowChrome>
    <WindowChrome CaptionHeight="42" CornerRadius="0"
                  GlassFrameThickness="0" ResizeBorderThickness="6"
                  UseAeroCaptionButtons="False"/>
  </WindowChrome.WindowChrome>

  <Border CornerRadius="13" ClipToBounds="True"
          BorderBrush="#10FFFFFF" BorderThickness="1">
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height="42"/>   <!-- title bar -->
        <RowDefinition Height="*"/>    <!-- body -->
      </Grid.RowDefinitions>

      <!-- §3 background sits behind everything; place it as first child spanning all rows -->
      <!-- §4 title bar in row 0 -->
      <!-- §5 body (list + now playing) in row 1 -->
    </Grid>
  </Border>
</Window>
```

Title-bar buttons must opt out of the drag region:
`WindowChrome.IsHitTestVisibleInChrome="True"` on each button. Wire:
- minimize → `WindowState = Minimized`
- close → `Close()`
- the title text/area is draggable automatically (it's in the caption height).

```xml
<!-- Title bar (Grid.Row=0) -->
<Grid Grid.Row="0" Panel.ZIndex="2">
  <StackPanel Orientation="Horizontal" Margin="16,0,0,0"
              VerticalAlignment="Center" HorizontalAlignment="Left">
    <Border Width="22" Height="22" CornerRadius="6">
      <Border.Background>
        <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
          <GradientStop Color="#FF62D2C4" Offset="0"/>
          <GradientStop Color="#FF2F8F86" Offset="1"/>
        </LinearGradientBrush>
      </Border.Background>
      <Path Data="{StaticResource RadioGeometry}" Stretch="Uniform"
            Width="14" Height="14" Stroke="#FF04201C" StrokeThickness="1.4"/>
    </Border>
    <TextBlock Margin="9,0,0,0" VerticalAlignment="Center" FontSize="12"
               FontWeight="SemiBold" Foreground="#FFCFE0DC">
      <Run Text="AURORA"/><Run Text=" RADIO" Foreground="#FF5C7873" FontWeight="Medium"/>
    </TextBlock>
  </StackPanel>

  <StackPanel Orientation="Horizontal" Margin="0,0,8,0"
              VerticalAlignment="Center" HorizontalAlignment="Right">
    <Button Style="{StaticResource TitleIconButton}" Content="{StaticResource SettingsGeometry}"/>
    <Button Style="{StaticResource TitleIconButton}" Content="{StaticResource InfoGeometry}"/>
    <Button Style="{StaticResource TitleIconButton}" Click="Minimize_Click"
            Content="{StaticResource MinGeometry}"/>
    <Button Style="{StaticResource TitleIconButton}" Click="Close_Click"
            Content="{StaticResource CloseGeometry}" Foreground="#FF9AACA8"/>
  </StackPanel>
</Grid>
```

---

## 3. Ambient wave background

Add `assets/player-bg.png` as a `Resource` build action. Layer it the same way the
mock does: base color → image → three gradient scrims (so the list stays readable
on the left and the now-playing copy stays readable in the center band).

Place this as the **first child of the body Grid (Grid.Row=1) spanning both list and
now-playing columns**, with `Panel.ZIndex="0"`. Actually simplest: put it in the body
Grid behind the two columns.

```xml
<Grid Grid.Row="1">
  <!-- 0. ambient background (spans full body) -->
  <Grid Panel.ZIndex="0">
    <Rectangle Fill="{StaticResource WindowBg}"/>
    <Image Source="/assets/player-bg.png" Stretch="UniformToFill"
           HorizontalAlignment="Right"/>
    <!-- radial vignette toward bottom-right glow -->
    <Rectangle>
      <Rectangle.Fill>
        <RadialGradientBrush Center="0.88,0.78" GradientOrigin="0.88,0.78"
                             RadiusX="1.3" RadiusY="1.0">
          <GradientStop Color="#00050D0C" Offset="0.30"/>
          <GradientStop Color="#80050D0C" Offset="1"/>
        </RadialGradientBrush>
      </Rectangle.Fill>
    </Rectangle>
    <!-- horizontal scrim: dark on left (list), fades out to the right -->
    <Rectangle>
      <Rectangle.Fill>
        <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
          <GradientStop Color="#FF050D0C" Offset="0"/>
          <GradientStop Color="#DB050D0C" Offset="0.26"/>
          <GradientStop Color="#A8050D0C" Offset="0.46"/>
          <GradientStop Color="#52050D0C" Offset="0.62"/>
          <GradientStop Color="#00050D0C" Offset="0.82"/>
        </LinearGradientBrush>
      </Rectangle.Fill>
    </Rectangle>
    <!-- top scrim -->
    <Rectangle VerticalAlignment="Top" Height="140">
      <Rectangle.Fill>
        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
          <GradientStop Color="#8C050D0C" Offset="0"/>
          <GradientStop Color="#00050D0C" Offset="1"/>
        </LinearGradientBrush>
      </Rectangle.Fill>
    </Rectangle>
  </Grid>

  <!-- 1. foreground columns (Panel.ZIndex=1) — see §5 -->
</Grid>
```

ARGB note: WPF hex is `#AARRGGBB`. CSS `rgba(5,13,12,0.86)` → alpha `0.86×255≈219≈0xDB`
→ `#DB050D0C`. The table above already did this conversion.

**Optional** subtle "drift": animate the `Image` with a small `TranslateTransform`
(`X: 0 → -10 → 0` over 26s, `RepeatBehavior=Forever`, `AutoReverse`). Keep it tiny;
`UniformToFill` overflow hides the edges.

---

## 4. Layout of the body (list + now playing)

```xml
<Grid Grid.Row="1">
  <!-- ambient from §3 here (ZIndex 0) -->
  <Grid Panel.ZIndex="1">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="362"/>
      <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>

    <!-- LEFT: list panel -->
    <Border Grid.Column="0" Background="{StaticResource PanelBg}"
            BorderBrush="{StaticResource Hairline}" BorderThickness="0,0,1,0">
      <DockPanel Margin="14,16,14,8">
        <!-- segmented tabs (DockPanel.Top) -->
        <!-- search field (DockPanel.Top) -->
        <!-- "Found 6 stations" caption (DockPanel.Top) -->
        <!-- ListBox fills remainder -->
      </DockPanel>
    </Border>

    <!-- RIGHT: now playing -->
    <Grid Grid.Column="1" Margin="44,34,44,30">
      <Grid.RowDefinitions>
        <RowDefinition Height="*"/>     <!-- now-playing block, vertically centered -->
        <RowDefinition Height="Auto"/>  <!-- transport + volume -->
      </Grid.RowDefinitions>
      <!-- see §6 -->
    </Grid>
  </Grid>
</Grid>
```

---

## 5. Station list (ListBox)

Strip the default ListBox chrome, then style the item container and data template.

```xml
<!-- Container: kills default selection look, gives rounded hover/selected states -->
<Style x:Key="StationItem" TargetType="ListBoxItem">
  <Setter Property="Padding" Value="0"/>
  <Setter Property="Margin" Value="0,1"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="ListBoxItem">
        <Border x:Name="Bd" CornerRadius="10" Padding="10"
                Background="Transparent">
          <ContentPresenter/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True">
            <Setter TargetName="Bd" Property="Background" Value="#0AFFFFFF"/>
          </Trigger>
          <Trigger Property="IsSelected" Value="True">
            <Setter TargetName="Bd" Property="Background" Value="{StaticResource TealTint10}"/>
            <!-- 2px teal left bar -->
            <Setter TargetName="Bd" Property="BorderBrush" Value="{StaticResource Teal}"/>
            <Setter TargetName="Bd" Property="BorderThickness" Value="2,0,0,0"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>

<ListBox ItemsSource="{Binding SearchResults}"
         SelectedItem="{Binding CurrentStation}"
         Background="Transparent" BorderThickness="0"
         ScrollViewer.HorizontalScrollBarVisibility="Disabled"
         ItemContainerStyle="{StaticResource StationItem}">
  <ListBox.ItemTemplate>
    <DataTemplate>
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="34"/>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>

        <!-- icon tile -->
        <Border Grid.Column="0" Width="34" Height="34" CornerRadius="9"
                Background="{StaticResource FillSubtle}"
                BorderBrush="{StaticResource HairlineSoft}" BorderThickness="1">
          <Path Data="{StaticResource RadioGeometry}" Stretch="Uniform"
                Width="17" Height="17" Stroke="#FF6F928C" StrokeThickness="1.4"/>
        </Border>

        <!-- title + subtitle -->
        <StackPanel Grid.Column="1" Margin="11,0" VerticalAlignment="Center">
          <TextBlock Text="{Binding Title}" FontSize="13.5" FontWeight="Medium"
                     Foreground="{StaticResource TextBody}"
                     TextTrimming="CharacterEllipsis"/>
          <TextBlock Text="{Binding Description}" FontSize="11.5" Margin="0,2,0,0"
                     Foreground="{StaticResource TextMuted}"
                     TextTrimming="CharacterEllipsis"/>
        </StackPanel>

        <!-- trailing add (+) button; replace with equalizer when IsPlaying -->
        <Button Grid.Column="2" Style="{StaticResource GhostSquareButton}"
                Command="{Binding DataContext.AddStation,
                          RelativeSource={RelativeSource AncestorType=ListBox}}"
                CommandParameter="{Binding}"
                Content="{StaticResource PlusGeometry}"/>
      </Grid>
    </DataTemplate>
  </ListBox.ItemTemplate>
</ListBox>
```

For the **active row's equalizer** (instead of the `+`), swap the trailing content with
a `DataTrigger` on `IsPlaying` showing the equalizer `UserControl` from §9, or use a
`ContentControl` whose template switches.

### Segmented tabs
Two `RadioButton`s (GroupName) styled as pills, or a 2-cell `Grid` of `ToggleButton`s.
Active = `Background={StaticResource TealTint16}`, `Foreground={StaticResource TealText}`,
1px inset teal border (`#4756C7BA`). Inactive = transparent, `Foreground=#FF7F948F`.
Wrap both in a `Border` `Background={FillSubtle}` `CornerRadius="11"` `Padding="3"`.

### Search field
```xml
<Border Height="40" CornerRadius="10" Background="{StaticResource FillSubtle}"
        BorderBrush="{StaticResource Hairline}" BorderThickness="1" Padding="12,0">
  <DockPanel>
    <Path DockPanel.Dock="Left" Data="{StaticResource SearchGeometry}" Stretch="Uniform"
          Width="16" Height="16" Stroke="#FF62A59B" StrokeThickness="1.6"
          VerticalAlignment="Center" Margin="0,0,9,0"/>
    <TextBox Background="Transparent" BorderThickness="0" VerticalContentAlignment="Center"
             Foreground="{StaticResource TextBody}" CaretBrush="{StaticResource Teal}"
             FontSize="13" Text="{Binding Query, UpdateSourceTrigger=PropertyChanged}"/>
  </DockPanel>
</Border>
```
(Placeholder text: use a `VisualBrush` watermark or a TextBlock overlay shown when empty.)

---

## 6. Now-playing block + transport

```xml
<!-- Row 0: now playing, vertically centered, max width ~440 -->
<StackPanel Grid.Row="0" VerticalAlignment="Center" MaxWidth="440" HorizontalAlignment="Left">
  <StackPanel Orientation="Horizontal" Margin="0,0,0,20">
    <TextBlock Text="NOW PLAYING" FontSize="11" FontWeight="SemiBold"
               Foreground="#FF6F8C87"/>
    <Path Data="{StaticResource CopyGeometry}" Stretch="Uniform" Width="14" Height="14"
          Stroke="#FF5D7A75" StrokeThickness="1.5" Margin="10,0,0,0"/>
  </StackPanel>

  <TextBlock Text="{Binding NowPlaying.Title}" FontSize="44" FontWeight="Light"
             Foreground="{StaticResource TextPrimary}" TextWrapping="Wrap" LineHeight="46"/>
  <TextBlock Text="{Binding NowPlaying.Artist}" FontSize="20" FontWeight="Light"
             Foreground="{StaticResource TextSecondary}" Margin="0,8,0,0"/>

  <!-- station chip -->
  <Border HorizontalAlignment="Left" Margin="0,22,0,0" CornerRadius="999"
          Padding="11,7,13,7" Background="#0DFFFFFF" BorderBrush="#17FFFFFF" BorderThickness="1">
    <StackPanel Orientation="Horizontal">
      <Path Data="{StaticResource RadioGeometry}" Stretch="Uniform" Width="15" Height="15"
            Stroke="{StaticResource TealBright}" StrokeThickness="1.4"/>
      <TextBlock Text="{Binding NowPlaying.StationLine}" Margin="8,0,0,0"
                 FontSize="12.5" Foreground="#FFCFDEDB" VerticalAlignment="Center"/>
    </StackPanel>
  </Border>

  <!-- status row: equalizer + LIVE pill + bitrate -->
  <StackPanel Orientation="Horizontal" Margin="0,18,0,0" VerticalAlignment="Center">
    <ContentControl Template="{StaticResource EqualizerTemplate}" Height="13"/>
    <Border Margin="12,0,0,0" CornerRadius="999" Padding="9,3" Background="#1F56C7BA"
            BorderBrush="#4056C7BA" BorderThickness="1">
      <StackPanel Orientation="Horizontal">
        <Ellipse x:Name="LiveDot" Width="6" Height="6" Fill="{StaticResource TealBright}"
                 VerticalAlignment="Center"/>
        <TextBlock Text="LIVE" Margin="6,0,0,0" FontSize="10.5" FontWeight="SemiBold"
                   Foreground="#FF8FE0D4"/>
      </StackPanel>
    </Border>
    <TextBlock Text="128 kbps · MP3" Margin="12,0,0,0" FontSize="12"
               Foreground="#FF6F8783" VerticalAlignment="Center"/>
  </StackPanel>
</StackPanel>

<!-- Row 1: transport + volume -->
<StackPanel Grid.Row="1">
  <StackPanel Orientation="Horizontal">
    <Button Style="{StaticResource RoundGhostButton}" Width="46" Height="46"
            Command="{Binding Prev}"  Content="{StaticResource PrevGeometry}"/>
    <Button Style="{StaticResource PlayButton}" Width="62" Height="62" Margin="16,0"
            Command="{Binding PlayPause}" Content="{StaticResource PauseGeometry}"/>
    <Button Style="{StaticResource RoundGhostButton}" Width="46" Height="46"
            Command="{Binding Next}"  Content="{StaticResource NextGeometry}"/>
    <Button Style="{StaticResource RoundGhostButton}" Width="46" Height="46" Margin="16,0,0,0"
            Command="{Binding Stop}"  Content="{StaticResource StopGeometry}"/>
  </StackPanel>

  <!-- volume -->
  <Grid Margin="0,24,0,0" MaxWidth="440" HorizontalAlignment="Left" Width="440">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="Auto"/>
      <ColumnDefinition Width="*"/>
      <ColumnDefinition Width="34"/>
    </Grid.ColumnDefinitions>
    <Path Grid.Column="0" Data="{StaticResource VolumeGeometry}" Stretch="Uniform"
          Width="18" Height="18" Fill="#FF8AA39E" VerticalAlignment="Center"/>
    <Slider Grid.Column="1" Style="{StaticResource VolumeSlider}" Margin="12,0"
            Minimum="0" Maximum="100" Value="{Binding Volume}"/>
    <TextBlock Grid.Column="2" Text="{Binding Volume, StringFormat={}{0:0}}"
               FontSize="12" Foreground="#FF8AA39E" HorizontalAlignment="Right"
               VerticalAlignment="Center"/>
  </Grid>
</StackPanel>
```

The play/pause glyph swaps via a `DataTrigger` on `IsPlaying` (Content = Pause vs Play geometry).

---

## 7. Button & slider styles

### Title-bar icon button
```xml
<Style x:Key="TitleIconButton" TargetType="Button">
  <Setter Property="Width" Value="30"/>
  <Setter Property="Height" Value="30"/>
  <Setter Property="Foreground" Value="#FF7F9A95"/>
  <Setter Property="WindowChrome.IsHitTestVisibleInChrome" Value="True"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="Button">
        <Border x:Name="b" CornerRadius="7" Background="Transparent">
          <Path Data="{TemplateBinding Content}" Stretch="Uniform" Width="16" Height="16"
                Stroke="{TemplateBinding Foreground}" StrokeThickness="1.6"
                StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True">
            <Setter TargetName="b" Property="Background" Value="#12FFFFFF"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
```
> For the **close** button, add a red hover (`#E81123`) and white stroke on hover.
> For *filled* glyphs (play/pause/stop/next), use `Fill` instead of `Stroke` on the `Path`.

### Round ghost transport button
```xml
<Style x:Key="RoundGhostButton" TargetType="Button">
  <Setter Property="Foreground" Value="#FFBCD0CC"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="Button">
        <Border x:Name="b" CornerRadius="999" Background="Transparent"
                BorderBrush="#1FFFFFFF" BorderThickness="1">
          <Path Data="{TemplateBinding Content}" Stretch="Uniform" Width="20" Height="20"
                Fill="{TemplateBinding Foreground}" HorizontalAlignment="Center"
                VerticalAlignment="Center"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True">
            <Setter TargetName="b" Property="Background" Value="#0FFFFFFF"/>
            <Setter TargetName="b" Property="BorderBrush" Value="#33FFFFFF"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
```

### Primary play button (teal + glow)
```xml
<Style x:Key="PlayButton" TargetType="Button">
  <Setter Property="Foreground" Value="#FF04201C"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="Button">
        <Grid>
          <Ellipse Fill="{StaticResource PlayBrush}">
            <Ellipse.Effect>
              <DropShadowEffect Color="#FF56C7BA" BlurRadius="28"
                                ShadowDepth="6" Direction="270" Opacity="0.6"/>
            </Ellipse.Effect>
          </Ellipse>
          <Path Data="{TemplateBinding Content}" Stretch="Uniform" Width="26" Height="26"
                Fill="{TemplateBinding Foreground}"
                HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
```

### Ghost square (+) button (list rows)
28×28, `CornerRadius=8`, `BorderBrush=#1AFFFFFF`, stroke icon `#FF8FB6AE`, hover teal.

### Volume slider
```xml
<Style x:Key="VolumeSlider" TargetType="Slider">
  <Setter Property="Height" Value="13"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="Slider">
        <Grid VerticalAlignment="Center">
          <Border Height="4" CornerRadius="3" Background="{StaticResource FillTrack}"/>
          <!-- filled portion -->
          <Border Height="4" CornerRadius="3" HorizontalAlignment="Left"
                  Background="{StaticResource VolumeFill}"
                  Width="{Binding RelativeSource={RelativeSource TemplatedParent},
                          Path=Value}"/> <!-- bind via converter to px width, see note -->
          <Track x:Name="PART_Track">
            <Track.Thumb>
              <Thumb Width="13" Height="13">
                <Thumb.Template>
                  <ControlTemplate TargetType="Thumb">
                    <Ellipse Fill="#FFEAFFFB">
                      <Ellipse.Effect>
                        <DropShadowEffect Color="Black" BlurRadius="6"
                                          ShadowDepth="2" Opacity="0.45"/>
                      </Ellipse.Effect>
                    </Ellipse>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
```
> Filled-track width: the simplest robust approach is a `RepeatButton`-based template
> (`PART_SelectionRange`) or an `IValueConverter` that maps `Value/Maximum × ActualWidth`.
> For a fixed-width slider you can bind width to a `MultiBinding(Value, ActualWidth, Maximum)`.

---

## 8. ViewModel surface (MVVM bindings used above)

```csharp
public class StationVM {
    public string Title { get; set; }
    public string Description { get; set; }
    public bool   IsPlaying { get; set; }
    public string Bitrate { get; set; }   // "128k MP3"
}

public class NowPlayingVM {
    public string Title { get; set; }       // "Dark Bird Is Home"
    public string Artist { get; set; }      // "The Tallest Man on Earth"
    public string StationLine { get; set; } // "SomaFM Folk Forward · 128k MP3"
}

public class PlayerViewModel : INotifyPropertyChanged {
    public ObservableCollection<StationVM> SearchResults { get; }
    public StationVM CurrentStation { get; set; }
    public NowPlayingVM NowPlaying { get; set; }
    public string Query { get; set; }
    public double Volume { get; set; } = 62;
    public bool IsPlaying { get; set; } = true;

    public ICommand PlayPause { get; }
    public ICommand Next { get; }
    public ICommand Prev { get; }
    public ICommand Stop { get; }
    public ICommand AddStation { get; }     // CommandParameter = StationVM
}
```
Implement audio with **NAudio** or `MediaPlayer` for the actual stream playback;
internet-radio "track title" comes from ICY/SHOUTcast metadata (StreamTitle).
Because radio is live, there is **no seek bar** — the `LIVE` badge replaces it
(intentional UX decision in this redesign).

---

## 9. Equalizer & LIVE animations

Equalizer = 3–4 narrow `Rectangle`s with `RenderTransformOrigin="0.5,1"` and a looping
`ScaleY` storyboard, staggered by `BeginTime`.

```xml
<ControlTemplate x:Key="EqualizerTemplate">
  <StackPanel Orientation="Horizontal" Height="13" VerticalAlignment="Bottom">
    <StackPanel.Resources>
      <Style TargetType="Rectangle">
        <Setter Property="Width" Value="2.6"/>
        <Setter Property="Height" Value="13"/>
        <Setter Property="Margin" Value="0,0,2.5,0"/>
        <Setter Property="Fill" Value="{StaticResource Teal}"/>
        <Setter Property="RadiusX" Value="2"/>
        <Setter Property="RadiusY" Value="2"/>
        <Setter Property="RenderTransformOrigin" Value="0.5,1"/>
        <Setter Property="RenderTransform"><Setter.Value><ScaleTransform/></Setter.Value></Setter>
      </Style>
    </StackPanel.Resources>
    <Rectangle x:Name="b1"/><Rectangle x:Name="b2"/><Rectangle x:Name="b3"/><Rectangle x:Name="b4"/>
  </StackPanel>
  <ControlTemplate.Triggers>
    <EventTrigger RoutedEvent="FrameworkElement.Loaded">
      <BeginStoryboard>
        <Storyboard RepeatBehavior="Forever" AutoReverse="True">
          <DoubleAnimation Storyboard.TargetName="b1"
            Storyboard.TargetProperty="RenderTransform.ScaleY"
            From="0.25" To="1" Duration="0:0:0.45"/>
          <DoubleAnimation Storyboard.TargetName="b2" BeginTime="0:0:0.14"
            Storyboard.TargetProperty="RenderTransform.ScaleY"
            From="0.25" To="1" Duration="0:0:0.45"/>
          <DoubleAnimation Storyboard.TargetName="b3" BeginTime="0:0:0.28"
            Storyboard.TargetProperty="RenderTransform.ScaleY"
            From="0.25" To="1" Duration="0:0:0.45"/>
          <DoubleAnimation Storyboard.TargetName="b4" BeginTime="0:0:0.40"
            Storyboard.TargetProperty="RenderTransform.ScaleY"
            From="0.25" To="1" Duration="0:0:0.45"/>
        </Storyboard>
      </BeginStoryboard>
    </EventTrigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
```

LIVE dot pulse: animate the `Ellipse.Opacity` `1 → 0.28 → 1` over 1.6s, Forever.

---

## 10. Icon geometries (Path Data)

Define once as `Geometry` resources (in `Theme.xaml`). All use a 24×24 coordinate box;
`Stretch="Uniform"` scales them. Stroke icons: set `Stroke` + `StrokeThickness≈1.6`,
`StrokeStartLineCap/EndLineCap=Round`. Filled icons: set `Fill`.

```xml
<!-- Broadcast / radio (stroke) -->
<PathGeometry x:Key="RadioGeometry" Figures="M8.6 8.6a5 5 0 0 0 0 6.8 M15.4 8.6a5 5 0 0 1 0 6.8 M6.1 6.1a9 9 0 0 0 0 11.8 M17.9 6.1a9 9 0 0 1 0 11.8"/>
<!-- center dot: add a separate Ellipse Width=4 in the tile if you want the dot -->

<!-- Play (fill) -->
<PathGeometry x:Key="PlayGeometry" Figures="M8 5 L19 12 L8 19 Z"/>
<!-- Pause (fill) -->
<GeometryGroup x:Key="PauseGeometry">
  <RectangleGeometry Rect="8,5.5,3,13" RadiusX="1" RadiusY="1"/>
  <RectangleGeometry Rect="13,5.5,3,13" RadiusX="1" RadiusY="1"/>
</GeometryGroup>
<!-- Stop (fill) -->
<RectangleGeometry x:Key="StopGeometry" Rect="6.5,6.5,11,11" RadiusX="2.6" RadiusY="2.6"/>
<!-- Next (fill) -->
<GeometryGroup x:Key="NextGeometry">
  <PathGeometry Figures="M6.5 5.5 L15 12 L6.5 18.5 Z"/>
  <RectangleGeometry Rect="15.7,5.5,2.4,13" RadiusX="1" RadiusY="1"/>
</GeometryGroup>
<!-- Prev (fill) -->
<GeometryGroup x:Key="PrevGeometry">
  <PathGeometry Figures="M17.5 5.5 L9 12 L17.5 18.5 Z"/>
  <RectangleGeometry Rect="5.9,5.5,2.4,13" RadiusX="1" RadiusY="1"/>
</GeometryGroup>
<!-- Plus (stroke) -->
<PathGeometry x:Key="PlusGeometry" Figures="M12 6.5 L12 17.5 M6.5 12 L17.5 12"/>
<!-- Search (stroke) -->
<PathGeometry x:Key="SearchGeometry" Figures="M11 4.4 A6.6 6.6 0 1 0 11 17.6 A6.6 6.6 0 1 0 11 4.4 M16 16 L20.5 20.5"/>
<!-- Volume (fill body + stroke waves: split into two Paths) -->
<PathGeometry x:Key="VolumeGeometry" Figures="M4 9.5 L8 9.5 L13 5.5 L13 18.5 L8 14.5 L4 14.5 Z"/>
<PathGeometry x:Key="VolumeWaves"   Figures="M16 9.2a4 4 0 0 1 0 5.6 M18.4 7a7.2 7.2 0 0 1 0 10"/>
<!-- Copy (stroke) -->
<PathGeometry x:Key="CopyGeometry" Figures="M9 9 H18 a2 2 0 0 1 2 2 V20 H11 a2 2 0 0 1 -2 -2 Z M5 15 V6 a2 2 0 0 1 2 -2 H15"/>
<!-- Settings / sliders (stroke + 2 filled knobs) -->
<PathGeometry x:Key="SettingsGeometry" Figures="M4 8 H20 M4 16 H20"/>
<!-- Info (stroke) -->
<PathGeometry x:Key="InfoGeometry" Figures="M12 3.6 A8.4 8.4 0 1 0 12 20.4 A8.4 8.4 0 1 0 12 3.6 M12 11 V16"/>
<!-- Minimize (stroke) -->
<PathGeometry x:Key="MinGeometry" Figures="M6 12 H18"/>
<!-- Close (stroke) -->
<PathGeometry x:Key="CloseGeometry" Figures="M7 7 L17 17 M17 7 L7 17"/>
<!-- Heart (fill) -->
<PathGeometry x:Key="HeartGeometry" Figures="M12 20 C5 15.6 5 10.4 5 10.4 A3.7 3.7 0 0 1 12 7.2 A3.7 3.7 0 0 1 19 10.4 C19 15.6 12 20 12 20 Z"/>
```
> For the volume icon, render two `Path`s in a `Grid` (filled body + stroked waves).
> For settings, draw the two stroke lines plus two small filled `Ellipse`s (knobs at
> ~9,8 and ~15,16). For info, add a small filled dot `Ellipse` at the top of the "i".
> Arc syntax (`A rx ry rot large sweep x y`) is valid WPF path mini-language.

---

## 11. Build / asset checklist

- [ ] `assets/player-bg.png` → **Build Action: Resource**
- [ ] `Fonts/HankenGrotesk-*.ttf` → **Resource**; reference `#Hanken Grotesk`
- [ ] `Theme.xaml` ResourceDictionary merged in `App.xaml` (brushes, geometries, styles)
- [ ] `WindowChrome` wired; min/close handlers; `IsHitTestVisibleInChrome` on chrome buttons
- [ ] Per-monitor DPI awareness in `app.manifest` for crisp text on hi-DPI
- [ ] Test resize down to MinWidth/MinHeight — list scrolls, now-playing column flexes

## 12. Fidelity notes / gotchas
- **No `letter-spacing`** in WPF — tracked caps labels will look slightly tighter. Acceptable.
- **No CSS `backdrop-filter`** — the left panel uses a flat semi-opaque fill (`PanelBg`)
  instead of a live blur. If you want true blur behind the panel, render a blurred copy
  of the background image behind it; usually not worth it.
- **Colors are ARGB** (`#AARRGGBB`). Don't drop the alpha byte.
- Filled vs stroke icons: filled glyphs (play/pause/stop/next/heart/volume body) use
  `Fill`; outline glyphs (radio/search/plus/copy/info/min/close) use `Stroke`.
- The bright wave crest can reduce text contrast — the three background scrims in §3 are
  tuned to keep the now-playing copy readable. Keep them.
```
