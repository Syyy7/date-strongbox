using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SecureMemo.App;

public sealed partial class RecordBadge : UserControl
{
    public static readonly DependencyProperty AppearanceProperty = DependencyProperty.Register(
        nameof(Appearance), typeof(RecordAppearance), typeof(RecordBadge), new PropertyMetadata(null));
    public RecordAppearance? Appearance
    {
        get => (RecordAppearance?)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }
    public RecordBadge() => InitializeComponent();
}
