using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace SecureMemo.App;

public sealed partial class HoldPasswordField : UserControl
{
    private static HoldPasswordField? revealed;
    internal PasswordBox Editor => Input;

    public HoldPasswordField()
    {
        InitializeComponent();
        Eye.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, (_, _) =>
        {
            if (Eye.IsPressed && IsEnabled)
            {
                HideActive();
                revealed = this;
                Input.PasswordRevealMode = PasswordRevealMode.Visible;
            }
            else Hide();
        });
        Eye.AddHandler(PointerMovedEvent, new PointerEventHandler(CheckBounds), true);
        Eye.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => Hide()), true);
        Eye.PointerCanceled += (_, _) => Hide();
        Eye.PointerCaptureLost += (_, _) => Hide();
        Eye.LostFocus += (_, _) => Hide();
        IsEnabledChanged += (_, _) => { if (!IsEnabled) Hide(); };
        Unloaded += (_, _) => Hide();
    }

    public string Header
    {
        get => Input.Header as string ?? "";
        set
        {
            Input.Header = value;
            AutomationProperties.SetName(Eye, $"按住查看{value}");
        }
    }

    internal string Password
    {
        get => Input.Password;
        set { Hide(); Input.Password = value; }
    }

    private void CheckBounds(object sender, PointerRoutedEventArgs e)
    {
        if (revealed != this) return;
        var point = e.GetCurrentPoint(Eye);
        var p = point.Position;
        if (p.X < 0 || p.Y < 0 || p.X >= Eye.ActualWidth || p.Y >= Eye.ActualHeight) Hide();
    }

    internal static void HideActive() => revealed?.Hide();

    private void Hide()
    {
        if (revealed != this) return;
        Input.PasswordRevealMode = PasswordRevealMode.Hidden;
        revealed = null;
    }
}
