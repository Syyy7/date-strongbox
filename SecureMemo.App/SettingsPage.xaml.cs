using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Win32;

namespace SecureMemo.App;

public sealed partial class SettingsPage : Page
{
    private const string Preferences = @"Software\SecureMemo\Preferences";
    internal event Action<ElementTheme>? ThemeChanged;
    internal event Action? ReturnRequested;
    internal event Action<int,int>? SizesChanged;
    internal event Action? ExportRequested, ImportRequested, PasswordRequested;
    private bool updating;
    private int appliedFont = AppearanceMetrics.DefaultFontSize, appliedScale = AppearanceMetrics.DefaultScale;
    private bool unlocked;

    public SettingsPage()
    {
        InitializeComponent();
        SetDraft(appliedFont, appliedScale);
    }

    internal ElementTheme LoadPreference()
    {
        ElementTheme theme = ElementTheme.Default;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Preferences);
            if (key?.GetValue("FontSize") is int f && f is >= 12 and <= 20) appliedFont = f;
            if (key?.GetValue("UiScale") is int s && s is >= 85 and <= 130) appliedScale = s;
            // A single registry value commits both sliders together. Older installs used two DWORDs.
            if (key?.GetValue("AppearanceSizes") is string[] { Length: 2 } values &&
                int.TryParse(values[0], out var font) && font is >= 12 and <= 20 &&
                int.TryParse(values[1], out var scale) && scale is >= 85 and <= 130)
                (appliedFont, appliedScale) = (font, scale);
            theme = key?.GetValue("Theme") switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowError("无法读取主题设置，已使用系统主题。");
        }
        updating = true;
        try
        {
            ThemeOptions.SelectedItem = ThemeOptions.Items.OfType<RadioButton>()
                .First(item => Equals(item.Tag, theme.ToString()));
            SizesChanged?.Invoke(appliedFont, appliedScale);
        }
        finally { updating = false; }
        SetDraft(appliedFont, appliedScale);
        return theme;
    }

    private void ThemeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updating || ThemeChanged == null || ThemeOptions.SelectedItem is not RadioButton item ||
            !Enum.TryParse<ElementTheme>(item.Tag as string, out var theme)) return;
        ThemeChanged(theme);
        SavePreference("Theme", theme.ToString(), RegistryValueKind.String);
    }

    private void SetDraft(int font, int scale)
    {
        FontSlider.Value = font;
        ScaleSlider.Value = scale;
        UpdatePreview();
    }

    private void PreviewChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ApplySizes != null) UpdatePreview();
    }

    private void UpdatePreview()
    {
        int font = (int)FontSlider.Value, scale = (int)ScaleSlider.Value;
        ((AppearanceMetrics)Resources["Draft"]).Set(font, scale);
        FontValue.Text = font.ToString();
        ScaleValue.Text = $"{scale}%";
        ApplySizes.IsEnabled = font != appliedFont || scale != appliedScale;
        DefaultSizes.IsEnabled = font != AppearanceMetrics.DefaultFontSize || scale != AppearanceMetrics.DefaultScale;
    }

    private void ResetSizes(object sender, RoutedEventArgs e) =>
        SetDraft(AppearanceMetrics.DefaultFontSize, AppearanceMetrics.DefaultScale);
    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        int font = (int)FontSlider.Value, scale = (int)ScaleSlider.Value;
        if (!unlocked || !SavePreference("AppearanceSizes", new[] { font.ToString(), scale.ToString() }, RegistryValueKind.MultiString)) return;
        (appliedFont, appliedScale) = (font, scale);
        SizesChanged?.Invoke(font, scale);
        UpdatePreview();
        ArrangeSections(ActualWidth);
    }

    private bool SavePreference(string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Preferences);
            key.SetValue(name, value, kind);
            SettingsError.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowError("无法保存外观设置，请检查当前用户的权限。");
            return false;
        }
    }

    private void ShowError(string text)
    {
        SettingsError.Text = text;
        SettingsError.Visibility = Visibility.Visible;
    }

    internal void FocusBack() => ReturnButton.Focus(FocusState.Programmatic);
    internal void Configure(bool unlocked, double availableWidth)
    {
        this.unlocked = unlocked;
        PageTitle.Text = unlocked ? "设置" : "颜色设置";
        VaultOptions.Visibility = VaultActions.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.MaxWidth = unlocked ? 860 : 448;
        SetDraft(appliedFont, appliedScale);
        ArrangeSections(availableWidth);
        Report("");
    }
    private void PageResized(object sender, SizeChangedEventArgs e) => ArrangeSections(e.NewSize.Width);
    private void ArrangeSections(double availableWidth)
    {
        double inset = SettingsCard.Padding.Left + SettingsCard.Padding.Right;
        double contentWidth = Math.Min(SettingsScroll.MaxWidth, availableWidth - 28) - inset - 6;
        bool wide = unlocked && contentWidth >= 570 + appliedFont * 5;
        OtherColumn.Width = wide ? new GridLength(.78, GridUnitType.Star) : new GridLength(0);
        Sections.ColumnSpacing = wide ? 28 : 0;
        Sections.RowSpacing = unlocked && !wide ? 20 : 0;
        Grid.SetColumn(OtherSettings, wide ? 1 : 0);
        Grid.SetRow(OtherSettings, unlocked && !wide ? 1 : 0);
    }
    internal void Report(string text)
    {
        Feedback.Text = text;
        Feedback.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ExportClick(object sender, RoutedEventArgs e) => ExportRequested?.Invoke();
    private void ImportClick(object sender, RoutedEventArgs e) => ImportRequested?.Invoke();
    private void ChangePasswordClick(object sender, RoutedEventArgs e) => PasswordRequested?.Invoke();
    private void ReturnClick(object sender, RoutedEventArgs e) => ReturnRequested?.Invoke();
}
