using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.ViewModels;

namespace EthernetPacketGenerator;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Loaded  += OnLoaded;
        Closing += (_, _) => ViewModel.TestCaseMgrVM.AutoSave();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BlockBuilder.SelectedBlockChanged += (_, block) =>
            ViewModel.SelectedBlock = block;

        // 팔레트 클릭 시 끝에 블록 추가 (드래그 불가 환경 대비)
        ProtocolPalette.ProtocolAddRequested += (_, type) =>
            ViewModel.BlockBuilderVM.InsertBlock(type, int.MaxValue);

        // 시퀀스 터미널 자동 스크롤
        ViewModel.PropertyChanged += OnMainVmPropertyChanged;
    }

    private void OnMainVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SequenceTerminalLog))
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (SequenceTerminalScroller != null)
                    SequenceTerminalScroller.ScrollToBottom();
            });
        }
    }

    // 시나리오랩 터미널 입력 — HyperTerminal과 동일하게 Enter 키 전송
    private void ScenarioInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var vm = ViewModel.HyperTerminalVM;
        if (vm.SendLineCommand.CanExecute(null))
            vm.SendLineCommand.Execute(null);
        e.Handled = true;
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();
}
