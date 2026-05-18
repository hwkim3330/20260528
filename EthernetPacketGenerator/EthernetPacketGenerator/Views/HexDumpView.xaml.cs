using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EthernetPacketGenerator.ViewModels;

namespace EthernetPacketGenerator.Views;

public partial class HexDumpView : UserControl
{
    public HexDumpView()
    {
        InitializeComponent();
    }

    private HexDumpViewModel? VM => DataContext as HexDumpViewModel;

    private void HexCell_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is HexCell cell)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                CommitCell(tb, cell);
                e.Handled = true;
            }
        }
    }

    private void HexCell_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.SelectAll();
    }

    private void CommitCell(TextBox tb, HexCell cell)
    {
        if (byte.TryParse(tb.Text, System.Globalization.NumberStyles.HexNumber, null, out byte val))
            VM?.EditByte(cell.Offset, val);
    }
}
