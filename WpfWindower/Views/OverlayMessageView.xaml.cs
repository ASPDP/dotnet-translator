using System.Windows.Controls;

namespace WpfWindower.Views;

public partial class OverlayMessageView : UserControl
{
    public OverlayMessageView()
    {
        InitializeComponent();
    }

    public Border OverlayBorder => OverlayBorderElement;

    public Button CloseButton => CloseButtonElement;


    public ItemsControl OverlayItems => OverlayItemsControl;


    public ItemsControl VariantsItems => VariantsItemsControl;

	private void CloseButtonElement_Click(object sender, System.Windows.RoutedEventArgs e)
	{

	}
}
