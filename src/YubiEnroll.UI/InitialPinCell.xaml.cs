using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace YubiEnroll.UI;

public partial class InitialPinCell : UserControl
{
    private static ToggleButton? _revealedPin;

    public InitialPinCell() => InitializeComponent();

    internal static void HidePin()
    {
        if (_revealedPin is { } pin) pin.IsChecked = false;
    }
    private void Pin_Checked(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_revealedPin, sender)) HidePin();
        _revealedPin = (ToggleButton)sender;
    }
    private void Pin_Unchecked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_revealedPin, sender)) _revealedPin = null;
    }
    private void PinContext_Changed(object sender, DependencyPropertyChangedEventArgs e) =>
        ((ToggleButton)sender).IsChecked = false;
    private void Pin_Unloaded(object sender, RoutedEventArgs e) =>
        ((ToggleButton)sender).IsChecked = false;
}
