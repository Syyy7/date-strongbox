using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace SecureMemo.App;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class AppearanceMetrics : INotifyPropertyChanged
{
    internal const int DefaultFontSize = 14;
    internal const int DefaultScale = 100;
    public int FontSize { get; private set; } = DefaultFontSize;
    public int Scale { get; private set; } = DefaultScale;
    public double Body => FontSize;
    public double Caption => Math.Max(11, Body - 2);
    public double Subtitle => Body + 6;
    public double Title => Body + 14;
    public double Gap => Scaled(12);
    public double BadgeSide => Scaled(36);
    public double IconSize => Scaled(18);
    public double FieldHeight => Math.Max(Body + 16, Scaled(40));
    public double ButtonHeight => Math.Max(Body + 16, Scaled(36));
    public Thickness FieldInset => new(Scaled(12), Scaled(7), Scaled(12), Scaled(7));
    public Thickness ButtonInset => new(Scaled(14), Scaled(5), Scaled(14), Scaled(5));
    public Thickness CardInset => new(Scaled(18));
    public Thickness PanelInset => new(Scaled(24));
    public Thickness RowInset => new(0, Scaled(8), 0, Scaled(8));

    private double Scaled(double value) => Math.Round(value * Scale / 100, 1);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Set(int font, int scale)
    {
        if (font == FontSize && scale == Scale) return;
        FontSize = font;
        Scale = scale;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }
}
