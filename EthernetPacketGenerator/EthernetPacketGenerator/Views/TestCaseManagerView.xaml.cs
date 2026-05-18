using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Views;

public partial class TestCaseManagerView : UserControl
{
    public TestCaseManagerView() => InitializeComponent();

    private void Triangle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TestCaseGroup group)
            group.IsExpanded = !group.IsExpanded;
        e.Handled = true;
    }
}
