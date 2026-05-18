using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.ViewModels;

namespace EthernetPacketGenerator.Views;

public partial class PacketListView : UserControl
{
    // ── ShowSendControls 의존성 프로퍼티 ─────────────────────────────────────
    // 기본값 True: 시나리오랩에서 Send 행 표시
    // False 설정 시 (패킷 제너레이터): Send 행 숨김, 패킷 조작 전용 툴바 표시
    public static readonly DependencyProperty ShowSendControlsProperty =
        DependencyProperty.Register(
            nameof(ShowSendControls),
            typeof(bool),
            typeof(PacketListView),
            new PropertyMetadata(true, OnShowSendControlsChanged));

    public bool ShowSendControls
    {
        get => (bool)GetValue(ShowSendControlsProperty);
        set => SetValue(ShowSendControlsProperty, value);
    }

    private static void OnShowSendControlsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PacketListView view) return;
        bool show = (bool)e.NewValue;
        view.SendControlRow.Visibility    = show ? Visibility.Visible   : Visibility.Collapsed;
        view.PacketOnlyToolbar.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        // SendVM 재연결 (Loaded 이후에 ShowSendControls 가 변경될 경우 대비)
        view.SendVM = view.GetSendVM();
        view.WireSendVmBindings();
    }

    // ── SendVM 프로퍼티 — XAML에서 ElementName=Root 로 접근 ──────────────────
    public static readonly DependencyProperty SendVMProperty =
        DependencyProperty.Register(
            nameof(SendVM),
            typeof(SendViewModel),
            typeof(PacketListView),
            new PropertyMetadata(null));

    public SendViewModel? SendVM
    {
        get => (SendViewModel?)GetValue(SendVMProperty);
        set => SetValue(SendVMProperty, value);
    }

    public PacketListView()
    {
        InitializeComponent();
        Loaded += PacketListView_Loaded;
    }

    private void PacketListView_Loaded(object sender, RoutedEventArgs e)
    {
        SendVM = GetSendVM();
        WireSendVmBindings();
    }

    private SendViewModel? _wiredSendVM;

    private void WireSendVmBindings()
    {
        var vm = SendVM;
        if (vm == null) return;
        if (ReferenceEquals(vm, _wiredSendVM)) return; // 이미 연결됨

        if (_wiredSendVM != null)
            _wiredSendVM.PropertyChanged -= SendVM_PropertyChanged;
        _wiredSendVM = vm;

        // CyclePeriodMs
        var bCycle = new System.Windows.Data.Binding(nameof(SendViewModel.CyclePeriodMs))
        {
            Source = vm,
            Mode   = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
        };
        CyclePeriodBox.SetBinding(TextBox.TextProperty, bCycle);

        // EstimatedTimeMs
        var bEst = new System.Windows.Data.Binding(nameof(SendViewModel.EstimatedTimeMs))
        {
            Source = vm,
            Mode   = System.Windows.Data.BindingMode.OneWay
        };
        EstimatedTimeBlock.SetBinding(System.Windows.Controls.TextBlock.TextProperty, bEst);

        // RepeatEnabled
        var bRepeat = new System.Windows.Data.Binding(nameof(SendViewModel.RepeatEnabled))
        {
            Source = vm,
            Mode   = System.Windows.Data.BindingMode.TwoWay
        };
        RepeatCheckBox.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, bRepeat);

        // IsSendingSelected → button content/color
        vm.PropertyChanged += SendVM_PropertyChanged;
    }

    private void SendVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SendViewModel.IsSendingSelected))
            UpdateSendSelectedBtn();
        else if (e.PropertyName == nameof(SendViewModel.IsSendingList))
            UpdateSendSequenceBtn();
    }

    private void UpdateSendSelectedBtn()
    {
        var sending = SendVM?.IsSendingSelected ?? false;
        SendSelectedBtn.Content    = sending ? "■ Stop Selected" : "▶ Send Selected";
        SendSelectedBtn.Background = sending
            ? new SolidColorBrush(Color.FromRgb(0xAA, 0x22, 0x22))
            : null;
        SendSelectedBtn.BorderBrush = sending
            ? new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33))
            : null;
    }

    private void UpdateSendSequenceBtn()
    {
        var sending = SendVM?.IsSendingList ?? false;
        SendSequenceBtn.Content    = sending ? "■ Stop" : "▶ Send Sequence";
        SendSequenceBtn.Background = sending
            ? new SolidColorBrush(Color.FromRgb(0xAA, 0x22, 0x22))
            : null;
        SendSequenceBtn.BorderBrush = sending
            ? new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33))
            : null;
    }

    // ── Send 버튼 클릭 ────────────────────────────────────────────────────────
    private SendViewModel? GetSendVM()
    {
        var fe = this as FrameworkElement;
        while (fe != null)
        {
            if (fe.DataContext is MainViewModel mvm) return mvm.SendVM;
            fe = VisualTreeHelper.GetParent(fe) as FrameworkElement;
        }
        return null;
    }

    private void SendSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        var sendVm = GetSendVM();
        if (sendVm == null) return;

        // Stop 중이면 중단
        if (sendVm.IsSendingSelected)
        {
            sendVm.SendSelectedCommand.Execute(null);
            return;
        }

        // 체크된 아이템 없으면 안내
        var vm = DataContext as PacketListViewModel;
        if (vm != null && !vm.Sequence.Any(s => s.IsChecked))
        {
            MessageBox.Show("체크된 항목이 없습니다.\n체크박스를 선택한 후 다시 시도하세요.",
                "Send Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        sendVm.SendSelectedCommand.Execute(null);
    }

    private void SendSequenceBtn_Click(object sender, RoutedEventArgs e)
    {
        var sendVm = GetSendVM();
        if (sendVm == null) return;
        sendVm.SendListCommand.Execute(null);
    }

    // ── 체크박스 컬럼 헤더에 Select-All 체크박스 주입 ───────────────────────────
    private void SequenceList_Loaded(object sender, RoutedEventArgs e) { }

    // ── Select-All 헤더 체크박스 (HeaderTemplate 에서 버블링) ─────────────────
    private void SelectAllHeader_Checked(object sender, RoutedEventArgs e)
        => SetAllChecked(true);

    private void SelectAllHeader_Unchecked(object sender, RoutedEventArgs e)
        => SetAllChecked(false);

    private void SetAllChecked(bool value)
    {
        if (DataContext is PacketListViewModel vm)
            foreach (var item in vm.Sequence)
                item.IsChecked = value;
        CommandManager.InvalidateRequerySuggested();
    }

    // ── 각 행 체크박스 변경 ───────────────────────────────────────────────────
    private void RowCheckBox_Changed(object sender, RoutedEventArgs e)
        => CommandManager.InvalidateRequerySuggested();

    // ── Interface 팝업 체크박스 ──────────────────────────────────────────────
    private void IfaceCheckList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel panel) return;
        RebuildInterfaceCheckboxes(panel);
    }

    private void RebuildInterfaceCheckboxes(StackPanel panel)
    {
        panel.Children.Clear();

        var seqItem = panel.Tag as SequenceItem;
        if (seqItem?.Packet == null) return;

        var vm = DataContext as PacketListViewModel;
        if (vm == null) return;

        var packet = seqItem.Packet;

        var clearBtn = new Button
        {
            Content = "(Default)  — 모두 해제",
            FontSize = 11,
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 0, 0, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0xCC, 0xFF)),
            FontStyle = FontStyles.Italic
        };
        clearBtn.Click += (_, _) =>
        {
            packet.OutgoingInterfaceNames.Clear();
            packet.OnOutgoingInterfaceChanged();
            RebuildInterfaceCheckboxes(panel);
        };
        panel.Children.Add(clearBtn);

        foreach (var entry in vm.InterfaceEntries)
        {
            if (entry.IsDefaultSentinel) continue;
            var cb = new CheckBox
            {
                Content = entry.ShortName,
                FontSize = 11,
                Margin = new Thickness(2, 2, 2, 2),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xFF)),
                IsChecked = packet.OutgoingInterfaceNames.Contains(entry.ShortName),
                Tag = (packet, entry.ShortName)
            };
            cb.Checked   += InterfaceCheckBox_Changed;
            cb.Unchecked += InterfaceCheckBox_Changed;
            panel.Children.Add(cb);
        }
    }

    private void InterfaceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        if (cb.Tag is not (PacketItem packet, string shortName)) return;
        if (cb.IsChecked == true) packet.OutgoingInterfaceNames.Add(shortName);
        else                      packet.OutgoingInterfaceNames.Remove(shortName);
        packet.OnOutgoingInterfaceChanged();
    }

    private void InterfaceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;
        var popup = FindVisualSibling<Popup>(toggle);
        if (popup == null) return;
        var panel = popup.Child is Border border ? border.Child as StackPanel : null;
        if (panel != null) RebuildInterfaceCheckboxes(panel);
    }

    private static T? FindVisualSibling<T>(DependencyObject element) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(element);
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject element) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T t) return t;
            var found = FindVisualDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ── Inline rename ────────────────────────────────────────────────────────
    private void PacketName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginEdit(sender as FrameworkElement);
        e.Handled = true;
    }

    private void BeginEdit(FrameworkElement? element)
    {
        if (element == null) return;
        var grid = element.Parent as Grid;
        if (grid == null) return;
        var panel   = grid.FindName("PacketPanel") as UIElement;
        var editBox = grid.FindName("EditBox")     as TextBox;
        if (panel == null || editBox == null) return;
        panel.Visibility   = Visibility.Collapsed;
        editBox.Visibility = Visibility.Visible;
        editBox.SelectAll();
        editBox.Focus();
    }

    private void CommitEdit(TextBox editBox)
    {
        var grid  = editBox.Parent as Grid;
        var panel = grid?.FindName("PacketPanel") as UIElement;
        var newName = editBox.Text.Trim();
        if (!string.IsNullOrEmpty(newName) && editBox.DataContext is SequenceItem si && si.Packet != null)
            si.Packet.Name = newName;
        editBox.Visibility = Visibility.Collapsed;
        if (panel != null) panel.Visibility = Visibility.Visible;
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editBox) return;
        if (e.Key == Key.Enter)       { CommitEdit(editBox); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            var grid  = editBox.Parent as Grid;
            var panel = grid?.FindName("PacketPanel") as UIElement;
            editBox.Visibility = Visibility.Collapsed;
            if (panel != null) panel.Visibility = Visibility.Visible;
            e.Handled = true;
        }
    }

    private void EditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitEdit(tb);
    }
}
