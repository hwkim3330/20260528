using System.Windows.Controls;
using System.Windows.Input;

namespace EthernetPacketGenerator.Views;

public partial class HyperTerminalView : UserControl
{
    public HyperTerminalView()
    {
        InitializeComponent();
    }

    // Enter 키로 커맨드 전송
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (DataContext is ViewModels.HyperTerminalViewModel vm &&
            vm.SendLineCommand.CanExecute(null))
        {
            vm.SendLineCommand.Execute(null);
        }

        e.Handled = true;
    }
}
