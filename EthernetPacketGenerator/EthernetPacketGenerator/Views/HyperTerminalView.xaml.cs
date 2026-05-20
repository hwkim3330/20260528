using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EthernetPacketGenerator.Views;

public partial class HyperTerminalView : UserControl
{
    private bool _threeColumn = false;

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

    // 레이아웃 토글: 상하 배치 ↔ 좌우 3분할
    private void HtLayoutToggle_Click(object sender, RoutedEventArgs e)
    {
        _threeColumn = !_threeColumn;

        if (_threeColumn)
        {
            // 상하 → 좌우 (RegisterViewer | Splitter | Terminal)
            HtRegisterRow.Height  = new GridLength(1, GridUnitType.Star);
            HtSplitterRow.MinHeight = 0;
            HtSplitterRow.Height  = new GridLength(0);
            HtTerminalRow.MinHeight = 0;
            HtTerminalRow.Height  = new GridLength(0);

            HtRegisterCol.Width   = new GridLength(1, GridUnitType.Star);
            HtSplitterCol.Width   = new GridLength(6);
            HtTerminalCol.Width   = new GridLength(360, GridUnitType.Pixel);
            HtTerminalCol.MinWidth = 150;

            Grid.SetRow(HtRegisterBorder,  0); Grid.SetColumn(HtRegisterBorder,  0);
            Grid.SetRow(HtSplitterHandle,  0); Grid.SetColumn(HtSplitterHandle,  1);
            Grid.SetRow(HtTerminalBorder,  0); Grid.SetColumn(HtTerminalBorder,  2);

            HtSplitterHandle.ResizeDirection    = GridResizeDirection.Columns;
            HtSplitterHandle.HorizontalAlignment = HorizontalAlignment.Stretch;
            HtSplitterHandle.Cursor             = Cursors.SizeWE;

            HtLayoutToggle.Content  = "⊟";
            HtLayoutToggle.ToolTip  = "상하 레이아웃으로 전환";
        }
        else
        {
            // 좌우 → 상하 (RegisterViewer / Splitter / Terminal)
            HtRegisterRow.Height  = new GridLength(1, GridUnitType.Star);
            HtSplitterRow.MinHeight = 0;
            HtSplitterRow.Height  = new GridLength(6);
            HtTerminalRow.MinHeight = 80;
            HtTerminalRow.Height  = new GridLength(220, GridUnitType.Pixel);

            HtRegisterCol.Width   = new GridLength(1, GridUnitType.Star);
            HtSplitterCol.Width   = new GridLength(0);
            HtTerminalCol.Width   = new GridLength(0);
            HtTerminalCol.MinWidth = 0;

            Grid.SetRow(HtRegisterBorder,  0); Grid.SetColumn(HtRegisterBorder,  0);
            Grid.SetRow(HtSplitterHandle,  1); Grid.SetColumn(HtSplitterHandle,  0);
            Grid.SetRow(HtTerminalBorder,  2); Grid.SetColumn(HtTerminalBorder,  0);

            HtSplitterHandle.ResizeDirection    = GridResizeDirection.Rows;
            HtSplitterHandle.HorizontalAlignment = HorizontalAlignment.Stretch;
            HtSplitterHandle.Cursor             = Cursors.SizeNS;

            HtLayoutToggle.Content = "⊞";
            HtLayoutToggle.ToolTip = "3분할 레이아웃으로 전환";
        }
    }
}
