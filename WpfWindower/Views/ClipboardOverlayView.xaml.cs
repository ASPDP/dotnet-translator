using System.Windows;
using System.Windows.Controls;

namespace WpfWindower.Views;

public partial class ClipboardOverlayView : UserControl
{
    public ClipboardOverlayView()
    {
        InitializeComponent();
    }

    public Border OverlayBorder => OverlayBorderElement;
    public TextBlock MessageText => MessageTextBlock;
    public Button CloseButton => CloseButtonElement;

    private void CloseButtonElement_Click(object sender, RoutedEventArgs e)
    {
        // Event will be handled by MainWindow
    }
}
