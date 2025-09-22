using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfWindower.Views;

public partial class OverlayMessageView : UserControl
{
    public event EventHandler<bool>? VariantExplanationHoverChanged;

    public OverlayMessageView()
    {
        InitializeComponent();
    }

    public Border OverlayBorder => OverlayBorderElement;

    public Button CloseButton => CloseButtonElement;

    public ItemsControl OverlayItems => OverlayItemsControl;

    public ItemsControl VariantsItems => VariantsItemsControl;

    public ProgressBar ElapsedProgress => ElapsedProgressBarElement;

    private void VariantItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element &&
            element.DataContext is MainWindow.VariantDisplayItem item &&
            item.HasExplanation)
        {
            VariantExplanationHoverChanged?.Invoke(this, true);
        }
    }

    private void VariantItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element &&
            element.DataContext is MainWindow.VariantDisplayItem item &&
            item.HasExplanation)
        {
            VariantExplanationHoverChanged?.Invoke(this, false);
        }
    }

    private void CloseButtonElement_Click(object sender, System.Windows.RoutedEventArgs e)
    {

    }
}

