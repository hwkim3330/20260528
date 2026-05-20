using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.Services;
using EthernetPacketGenerator.ViewModels;

namespace EthernetPacketGenerator.Views;

public partial class PacketListView : UserControl
{
    // ── DependencyProperties ─────────────────────────────────────────────────

    public static readonly DependencyProperty ShowSendControlsProperty =
        DependencyProperty.Register(nameof(ShowSendControls), typeof(bool), typeof(PacketListView),
            new PropertyMetadata(false, OnShowSendControlsChanged));

    public bool ShowSendControls
    {
        get => (bool)GetValue(ShowSendControlsProperty);
        set => SetValue(ShowSendControlsProperty, value);
    }

    private static void OnShowSendControlsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PacketListView view)
        {
            view.SendInlinePanel.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            if ((bool)e.NewValue)
            {
                view.ApplyTcSelectorVisibility();
                if (view.SendVM != null)
                    view.WireSendButtons(view.SendVM);
            }
        }
    }

    public static readonly DependencyProperty SendVMProperty =
        DependencyProperty.Register(nameof(SendVM), typeof(SendViewModel), typeof(PacketListView),
            new PropertyMetadata(null, OnSendVMChanged));

    public SendViewModel? SendVM
    {
        get => (SendViewModel?)GetValue(SendVMProperty);
        set => SetValue(SendVMProperty, value);
    }

    private static void OnSendVMChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PacketListView view) return;
        if (e.OldValue is SendViewModel oldVm)
            oldVm.PropertyChanged -= view.OnSendVMPropertyChanged;
        if (e.NewValue is SendViewModel newVm)
        {
            newVm.PropertyChanged += view.OnSendVMPropertyChanged;
            view.WireSendButtons(newVm);
        }
    }

    // SendViewModel PropertyChanged → EstimatedTime 및 DropWarning 코드비하인드 처리
    // (버튼 Content/Visibility/Command는 XAML DataTrigger로 처리)
    private void OnSendVMPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not SendViewModel vm) return;
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(SendViewModel.EstimatedTimeMs):
                    if (!IsEthernetOnly) EstimatedLabel.Text = vm.EstimatedTimeMs;
                    break;
                case nameof(SendViewModel.IsDropWarning):
                case nameof(SendViewModel.IsOverrun):
                case nameof(SendViewModel.PassResultLabel):
                case nameof(SendViewModel.PassResultValue):
                    UpdateDropWarning(vm);
                    break;
                case nameof(SendViewModel.IsSendingSelected):
                    UpdateSendSelectedBtn(vm);
                    UpdateSendListBtn(vm);
                    UpdateSpinner(vm);
                    break;
                case nameof(SendViewModel.IsSendingList):
                    UpdateSendListBtn(vm);
                    UpdateSendSelectedBtn(vm);
                    UpdateSpinner(vm);
                    break;
                case nameof(SendViewModel.IsSending):
                    UpdateSpinner(vm);
                    break;
            }
        });
    }

    private static readonly System.Windows.Media.SolidColorBrush _btnIdleBrush =
        new(System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x40));
    private static readonly System.Windows.Media.SolidColorBrush _btnStopBrush =
        new(System.Windows.Media.Color.FromRgb(0x5A, 0x1A, 0x1A));

    internal void UpdateSendSelectedBtn(SendViewModel vm)
    {
        if (IsRunningSequence)
        {
            SendSelectedBtn.Content    = "▶  Send Selected";
            SendSelectedBtn.Background = _btnIdleBrush;
            SendSelectedBtn.IsEnabled  = false;
        }
        else
        {
            SendSelectedBtn.Content    = vm.SendSelectedLabel;
            SendSelectedBtn.Background = vm.IsSendingSelected ? _btnStopBrush : _btnIdleBrush;
            SendSelectedBtn.IsEnabled  = vm.IsSendingSelected || !vm.IsSendingList;
        }
    }

    internal void UpdateSendListBtn(SendViewModel vm)
    {
        if (IsRunningSequence)
        {
            SendListBtn.Content    = "▶  Send List";
            SendListBtn.Background = _btnIdleBrush;
            SendListBtn.IsEnabled  = false;
        }
        else
        {
            SendListBtn.Content    = vm.SendListLabel;
            SendListBtn.Background = vm.IsSendingList ? _btnStopBrush : _btnIdleBrush;
            SendListBtn.IsEnabled  = vm.IsSendingList || !vm.IsSendingSelected;
        }
    }

    private void UpdateSpinner(SendViewModel vm)
    {
        bool sending = vm.IsSending;
        SpinnerBorder.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        IdleDot.Visibility       = sending ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateDropWarning(SendViewModel vm)
    {
        if (UseSequenceTitle) return;   // 테스트 시퀀스 탭에서는 초과/여유 숨김

        bool showDrop = vm.IsDropWarning;
        DropWarningBorder.Visibility = showDrop ? Visibility.Visible : Visibility.Collapsed;

        if (vm.IsSendingList || vm.PassResultLabel != "-")
        {
            PassResultLabel.Visibility = Visibility.Visible;
            bool overrun = vm.IsOverrun;
            PassResultLabel.Text = $"{vm.PassResultLabel}: {vm.PassResultValue}";
            PassResultLabel.Foreground = overrun
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44))
                : new SolidColorBrush(Color.FromRgb(0x44, 0xFF, 0x88));
        }
        else
        {
            PassResultLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void WireSendButtons(SendViewModel vm)
    {
        DiagLog($"[PLV] WireSendButtons  IsLoaded={IsLoaded}  vm={vm.GetHashCode()}  ShowSendControls={ShowSendControls}");
        // Controls may not exist yet if called before InitializeComponent / before Loaded
        if (!IsLoaded) { DiagLog("[PLV] WireSendButtons skipped — not loaded yet"); return; }

        ApplyEstimatedTimeBinding();

        // CyclePeriodMs TwoWay binding
        CyclePeriodBox.SetBinding(TextBox.TextProperty, new Binding(nameof(SendViewModel.CyclePeriodMs))
        {
            Source = vm,
            Mode   = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
        });

        // RepeatEnabled TwoWay binding
        RepeatCheckBox.SetBinding(System.Windows.Controls.CheckBox.IsCheckedProperty,
            new Binding(nameof(SendViewModel.RepeatEnabled))
            {
                Source = vm,
                Mode   = BindingMode.TwoWay
            });

        // ContinueOnFail TwoWay binding
        ContinueOnFailCheckBox.SetBinding(System.Windows.Controls.CheckBox.IsCheckedProperty,
            new Binding(nameof(SendViewModel.ContinueOnFail))
            {
                Source = vm,
                Mode   = BindingMode.TwoWay
            });

        // StartTime / EndTime 바인딩
        StartTimeLabel.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(SendViewModel.StartTime)) { Source = vm });
        EndTimeLabel.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(SendViewModel.EndTime)) { Source = vm });

        // Initial button/spinner state
        UpdateSendSelectedBtn(vm);
        UpdateSendListBtn(vm);
        UpdateSpinner(vm);
        UpdateDropWarning(vm);

        CommandManager.InvalidateRequerySuggested();
    }

    // IsEthernetOnly=True → EthernetSequence, False → 전체 Sequence
    public static readonly DependencyProperty IsEthernetOnlyProperty =
        DependencyProperty.Register(nameof(IsEthernetOnly), typeof(bool), typeof(PacketListView),
            new PropertyMetadata(false, OnIsEthernetOnlyChanged));

    public bool IsEthernetOnly
    {
        get => (bool)GetValue(IsEthernetOnlyProperty);
        set => SetValue(IsEthernetOnlyProperty, value);
    }

    private static void OnIsEthernetOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PacketListView view)
        {
            view.ApplySequenceBinding();
            view.ApplyEstimatedTimeBinding();
            view.ApplyTcSelectorVisibility();
        }
    }

    // UseSequenceTitle=True → 헤더를 TestSequenceTitle로 바인딩
    public static readonly DependencyProperty UseSequenceTitleProperty =
        DependencyProperty.Register(nameof(UseSequenceTitle), typeof(bool), typeof(PacketListView),
            new PropertyMetadata(false, OnUseSequenceTitleChanged));

    public bool UseSequenceTitle
    {
        get => (bool)GetValue(UseSequenceTitleProperty);
        set => SetValue(UseSequenceTitleProperty, value);
    }

    private static void OnUseSequenceTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PacketListView view)
        {
            view.ApplyHeaderBinding();
            view.ApplyCyclePeriodVisibility();
        }
    }

    // IsRunningSequence=True → 이 뷰 전체 비활성화 (Run Seq 실행 중 버튼 잠금)
    public static readonly DependencyProperty IsRunningSequenceProperty =
        DependencyProperty.Register(nameof(IsRunningSequence), typeof(bool), typeof(PacketListView),
            new PropertyMetadata(false, OnIsRunningSequenceChanged));

    public bool IsRunningSequence
    {
        get => (bool)GetValue(IsRunningSequenceProperty);
        set => SetValue(IsRunningSequenceProperty, value);
    }

    private static void OnIsRunningSequenceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PacketListView view)
        {
            bool running = (bool)e.NewValue;
            view.PacketEditButtons.IsEnabled = !running;
            if (running)
            {
                view.SendSelectedBtn.IsEnabled = false;
                view.SendListBtn.IsEnabled     = false;
            }
            else if (view.SendVM != null)
            {
                view.UpdateSendSelectedBtn(view.SendVM);
                view.UpdateSendListBtn(view.SendVM);
            }
        }
    }

    // TcSelectedCommand — TC 선택 시 MainViewModel이 양쪽 VM을 동기화
    public static readonly DependencyProperty TcSelectedCommandProperty =
        DependencyProperty.Register(nameof(TcSelectedCommand),
            typeof(ICommand), typeof(PacketListView));

    public ICommand? TcSelectedCommand
    {
        get => (ICommand?)GetValue(TcSelectedCommandProperty);
        set => SetValue(TcSelectedCommandProperty, value);
    }

    // TcGroups — test-case tree source for the TC selector (Packet Generator tab only)
    public static readonly DependencyProperty TcGroupsProperty =
        DependencyProperty.Register(nameof(TcGroups),
            typeof(ObservableCollection<TestCaseGroup>), typeof(PacketListView),
            new PropertyMetadata(null, OnTcGroupsChanged));

    public ObservableCollection<TestCaseGroup>? TcGroups
    {
        get => (ObservableCollection<TestCaseGroup>?)GetValue(TcGroupsProperty);
        set => SetValue(TcGroupsProperty, value);
    }

    private static void OnTcGroupsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PacketListView view && view.IsLoaded)
            view.TcGroupTree.ItemsSource = e.NewValue as ObservableCollection<TestCaseGroup>;
    }

    private void ApplyTcSelectorVisibility()
    {
        if (!IsLoaded) return;
        bool showSelector = IsEthernetOnly && ShowSendControls;
        TcSelectorPanel.Visibility         = showSelector ? Visibility.Visible : Visibility.Collapsed;
        ContinueOnFailCheckBox.Visibility  = IsEthernetOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyCyclePeriodVisibility()
    {
        if (!IsLoaded) return;
        var hide = UseSequenceTitle ? Visibility.Collapsed : Visibility.Visible;
        CyclePeriodPanel.Visibility  = hide;
        PassResultLabel.Visibility   = UseSequenceTitle ? Visibility.Collapsed : PassResultLabel.Visibility;
        DropWarningBorder.Visibility = UseSequenceTitle ? Visibility.Collapsed : DropWarningBorder.Visibility;
    }

    private void ApplyHeaderBinding()
    {
        var propName = UseSequenceTitle
            ? nameof(PacketListViewModel.TestSequenceTitle)
            : nameof(PacketListViewModel.PacketListTitle);
        HeaderLabel.SetBinding(TextBlock.TextProperty, new Binding(propName));
    }

    private void ApplySequenceBinding()
    {
        if (DataContext is not PacketListViewModel vm) return;
        SequenceList.ItemsSource = IsEthernetOnly ? vm.EthernetSequence : vm.Sequence;
    }

    private void ApplyEstimatedTimeBinding()
    {
        if (IsEthernetOnly)
        {
            // 패킷 제너레이터 탭: EthernetSequence 기준 예상 시간
            EstimatedLabel.SetBinding(TextBlock.TextProperty,
                new Binding(nameof(PacketListViewModel.EthernetEstimatedTimeMs)));
        }
        else if (DataContext is PacketListViewModel vm && vm.HasInjectedEstimated)
        {
            // 시나리오 랩: TestCaseManagerVM이 주입한 합산/단일 예상 시간
            EstimatedLabel.SetBinding(TextBlock.TextProperty,
                new Binding(nameof(PacketListViewModel.EstimatedLabelText)));
        }
        else if (SendVM != null)
        {
            // 시나리오 랩 폴백: SendViewModel.EstimatedTimeMs
            EstimatedLabel.SetBinding(TextBlock.TextProperty,
                new Binding(nameof(SendViewModel.EstimatedTimeMs)) { Source = SendVM });
        }
        else
        {
            BindingOperations.ClearBinding(EstimatedLabel, TextBlock.TextProperty);
            EstimatedLabel.Text = "-";
        }
    }

    public PacketListView()
    {
        InitializeComponent();

        // DataContext 변경 시 바인딩 재적용
        DataContextChanged += (_, e) =>
        {
            // 이전 VM 구독 해제
            if (e.OldValue is PacketListViewModel oldVm)
                oldVm.PropertyChanged -= OnPacketListVMPropertyChanged;
            // 새 VM 구독
            if (e.NewValue is PacketListViewModel newVm)
                newVm.PropertyChanged += OnPacketListVMPropertyChanged;

            ApplySequenceBinding();
            ApplyHeaderBinding();
            ApplyEstimatedTimeBinding();
        };

        // UserControl 자체가 Loaded된 시점에 SendVM이 이미 설정돼 있으면 Wire
        Loaded   += OnControlLoaded;
        Loaded   += (_, _) => { if (!IsEthernetOnly) ActiveDropTarget = this; };
        Unloaded += (_, _) => { if (ActiveDropTarget == this) ActiveDropTarget = null; };
    }

    private void OnPacketListVMPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PacketListViewModel.HasInjectedEstimated) ||
            e.PropertyName == nameof(PacketListViewModel.InjectedEstimatedTimeMs))
        {
            Dispatcher.Invoke(ApplyEstimatedTimeBinding);
        }
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        DiagLog($"[PLV] OnControlLoaded  SendVM={SendVM?.GetHashCode().ToString() ?? "NULL"}  ShowSendControls={ShowSendControls}");
        // 탭이 처음 렌더될 때 한 번 실행 — 이 시점엔 SendVM 바인딩이 완료됨
        if (SendVM != null)
            WireSendButtons(SendVM);

        ApplySequenceBinding();
        ApplyHeaderBinding();
        ApplyEstimatedTimeBinding();
        ApplyCyclePeriodVisibility();
        ApplyTcSelectorVisibility();

        // TC 트리 ItemsSource 초기화
        if (TcGroups != null)
            TcGroupTree.ItemsSource = TcGroups;
    }

    // ── drag-drop: active drop target (for EventPaletteView cross-view drag) ──
    internal static PacketListView? ActiveDropTarget;

    // ── drag state ────────────────────────────────────────────────────────────
    private SequenceItem?        _pendingDragItem;
    private Point                _mouseDownPos;
    private bool                 _isDragging;
    private SequenceItem?        _dragItem;
    private int                  _insertIdx = -1;   // -1 = delete on drop
    private SeqItemAdornerWindow? _adorner;

    private void SequenceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 체크박스나 토글버튼 위에서 누른 경우 드래그 시작하지 않음
        if (e.OriginalSource is DependencyObject src)
        {
            var el = src;
            while (el != null)
            {
                if (el is CheckBox || el is ToggleButton) return;
                el = VisualTreeHelper.GetParent(el);
            }
        }

        var lvi = GetListViewItemFromPoint(e.GetPosition(SequenceList));
        if (lvi?.DataContext is not SequenceItem si) return;
        _pendingDragItem = si;
        _mouseDownPos    = e.GetPosition(this);
        _isDragging      = false;
        // CaptureMouse는 실제 드래그 시작(임계값 초과) 시에만 호출
    }

    private void SequenceList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDragItem == null && !_isDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }

        if (!_isDragging)
        {
            var pos = e.GetPosition(this);
            bool over =
                Math.Abs(pos.X - _mouseDownPos.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - _mouseDownPos.Y) > SystemParameters.MinimumVerticalDragDistance;
            if (!over) return;

            _isDragging      = true;
            _dragItem        = _pendingDragItem;
            _pendingDragItem = null;
            SequenceList.Cursor = Cursors.SizeNS;
            SequenceList.CaptureMouse();   // 드래그 확정 후 캡처

            // 어도너 창 생성
            if (_dragItem != null) _adorner = CreateAdorner(_dragItem);
            _adorner?.Show();
        }

        _adorner?.PlaceAtCursor(new Point(60, 12));

        // cursor inside list?
        var ptInList = e.GetPosition(SequenceList);
        if (new Rect(0, 0, SequenceList.ActualWidth, SequenceList.ActualHeight).Contains(ptInList))
        {
            _insertIdx = CalcInsertIdx(ptInList);
            ShowDropIndicator(_insertIdx);
        }
        else
        {
            _insertIdx = -1;
            HideDropIndicator();
        }

        e.Handled = true;
    }

    private void SequenceList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging && _dragItem != null)
        {
            var ptInList = e.GetPosition(SequenceList);
            if (!new Rect(0, 0, SequenceList.ActualWidth, SequenceList.ActualHeight).Contains(ptInList))
                _insertIdx = -1;
        }
        SequenceList.ReleaseMouseCapture();
        if (!_isDragging || _dragItem == null) { ResetDragState(); return; }
        ApplyDrop();
        ResetDragState();
        e.Handled = true;
    }

    private void SequenceList_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            _insertIdx = -1;
            HideDropIndicator();
        }
    }

    private void ApplyDrop()
    {
        var vm = DataContext as PacketListViewModel;
        if (vm == null || _dragItem == null) return;
        var seq = IsEthernetOnly ? vm.EthernetSequence : vm.Sequence;
        int from = seq.IndexOf(_dragItem);
        if (from < 0) return;

        if (_insertIdx < 0)
        {
            seq.RemoveAt(from);
        }
        else
        {
            int dest = _insertIdx > from ? _insertIdx - 1 : _insertIdx;
            if (dest != from && dest >= 0 && dest < seq.Count)
                seq.Move(from, dest);
        }
    }

    private void CancelDrag()
    {
        SequenceList.ReleaseMouseCapture();
        ResetDragState();
    }

    private void ResetDragState()
    {
        _pendingDragItem = null;
        _isDragging      = false;
        _dragItem        = null;
        _insertIdx       = -1;
        SequenceList.Cursor = Cursors.Arrow;
        HideDropIndicator();
        _adorner?.Close();
        _adorner = null;
    }

    private static SeqItemAdornerWindow? CreateAdorner(SequenceItem item)
    {
        try { return SeqItemAdornerWindow.Create(item); }
        catch { return null; }
    }

    // ── drop indicator ────────────────────────────────────────────────────────
    private void ShowDropIndicator(int insertIdx)
    {
        double y = GetIndicatorY(insertIdx);
        Canvas.SetTop(DropIndicatorLine, y);
        DropIndicatorCanvas.Visibility = Visibility.Visible;
    }

    private void HideDropIndicator()
    {
        DropIndicatorCanvas.Visibility = Visibility.Collapsed;
    }

    private double GetIndicatorY(int insertIdx)
    {
        var items = GetVisibleListViewItems();
        if (items.Count == 0) return 0;

        if (insertIdx <= 0)
        {
            try
            {
                var first = items[0];
                return first.TransformToAncestor(DropIndicatorCanvas)
                            .TransformBounds(new Rect(first.RenderSize)).Top;
            }
            catch { return 0; }
        }

        int t = Math.Min(insertIdx - 1, items.Count - 1);
        try
        {
            var child = items[t];
            return child.TransformToAncestor(DropIndicatorCanvas)
                        .TransformBounds(new Rect(child.RenderSize)).Bottom;
        }
        catch { return 0; }
    }

    private List<ListViewItem> GetVisibleListViewItems()
    {
        var result = new List<ListViewItem>();
        var panel  = FindVisualDescendant<VirtualizingStackPanel>(SequenceList);
        if (panel == null) return result;
        int count = VisualTreeHelper.GetChildrenCount(panel);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(panel, i) is ListViewItem lvi)
                result.Add(lvi);
        }
        return result;
    }

    // ── insert index: Y midpoint of visible rows ──────────────────────────────
    private int CalcInsertIdx(Point ptInList)
    {
        var vm  = DataContext as PacketListViewModel;
        var seq = IsEthernetOnly ? vm?.EthernetSequence : vm?.Sequence;
        if (seq == null) return 0;

        var items = GetVisibleListViewItems();
        for (int i = 0; i < items.Count; i++)
        {
            try
            {
                var b = items[i].TransformToAncestor(SequenceList)
                                .TransformBounds(new Rect(items[i].RenderSize));
                if (ptInList.Y < b.Top + b.Height / 2) return i;
            }
            catch { }
        }
        return seq.Count;
    }

    // ── PaletteDragOver/Drop/Leave (called from EventPaletteView) ─────────────
    internal void PaletteDragOver(SequenceEventType type, Point screenPx)
    {
        try
        {
            var ptInList = SequenceList.PointFromScreen(screenPx);
            if (new Rect(0, 0, SequenceList.ActualWidth, SequenceList.ActualHeight).Contains(ptInList))
            {
                _insertIdx = CalcInsertIdx(ptInList);
                ShowDropIndicator(_insertIdx);
            }
            else
            {
                _insertIdx = -1;
                HideDropIndicator();
            }
        }
        catch { }
    }

    internal void PaletteDrop(SequenceEventType type, Point screenPx)
    {
        HideDropIndicator();
        var vm = DataContext as PacketListViewModel;
        if (vm == null) return;

        try
        {
            var ptInList = SequenceList.PointFromScreen(screenPx);
            if (!new Rect(0, 0, SequenceList.ActualWidth, SequenceList.ActualHeight).Contains(ptInList))
                return;

            int idx = CalcInsertIdx(ptInList);
            var ev = new EthernetPacketGenerator.Models.SequenceEvent { EventType = type };
            var si = new EthernetPacketGenerator.Models.SequenceItem(ev);
            var seq = vm.Sequence;
            if (idx >= seq.Count) seq.Add(si);
            else seq.Insert(idx, si);
        }
        catch { }
        _insertIdx = -1;
    }

    internal void PaletteDragLeave()
    {
        _insertIdx = -1;
        HideDropIndicator();
    }

    private ListViewItem? GetListViewItemFromPoint(Point point)
    {
        var hit = VisualTreeHelper.HitTest(SequenceList, point);
        if (hit == null) return null;
        var dep = hit.VisualHit as DependencyObject;
        while (dep != null)
        {
            if (dep is ListViewItem lvi) return lvi;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return null;
    }

    // ── 체크박스 헤더 Select-All ──────────────────────────────────────────────
    private void SequenceList_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySequenceBinding();
        ApplyHeaderBinding();
        ApplyEstimatedTimeBinding();
    }

    private void SelectAllHeader_Checked(object sender, RoutedEventArgs e)
        => SetAllChecked(true);

    private void SelectAllHeader_Unchecked(object sender, RoutedEventArgs e)
        => SetAllChecked(false);

    private void SetAllChecked(bool value)
    {
        if (DataContext is PacketListViewModel vm)
        {
            var src = IsEthernetOnly ? vm.EthernetSequence : vm.Sequence;
            foreach (var item in src)
                item.IsChecked = value;
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void RowCheckBox_Changed(object sender, RoutedEventArgs e)
        => CommandManager.InvalidateRequerySuggested();

    // ── Interface 팝업 체크박스 ───────────────────────────────────────────────
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
            Content     = "(Default)  — 모두 해제",
            FontSize    = 11,
            Padding     = new Thickness(4, 2, 4, 2),
            Margin      = new Thickness(0, 0, 0, 4),
            Background  = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A)),
            Foreground  = new SolidColorBrush(Color.FromRgb(0x88, 0xCC, 0xFF)),
            FontStyle   = FontStyles.Italic
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
                Content    = entry.ShortName,
                FontSize   = 11,
                Margin     = new Thickness(2, 2, 2, 2),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xFF)),
                IsChecked  = packet.OutgoingInterfaceNames.Contains(entry.ShortName),
                Tag        = (packet, entry.ShortName)
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

    // ── TC 선택기 (패킷 제너레이터 탭) ───────────────────────────────────────
    private void TcSelectBtn_Click(object sender, RoutedEventArgs e)
    {
        // ItemsSource 최신화 후 팝업 토글
        TcGroupTree.ItemsSource = TcGroups;
        TcSelectPopup.IsOpen    = !TcSelectPopup.IsOpen;
    }

    private void TcClearBtn_Click(object sender, RoutedEventArgs e)
    {
        // 선택 초기화
        TcSelectBtn.Content   = "TC 선택 ▾";
        TcClearBtn.Visibility = Visibility.Collapsed;

        // PacketListVM을 기본 상태(빈 패킷 1개)로 리셋
        if (DataContext is PacketListViewModel vm)
        {
            vm.Sequence.Clear();
            vm.AddPacket();
        }
    }

    private void TcGroupTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TestCaseEntry tc) return;

        // 팝업 닫기
        TcSelectPopup.IsOpen = false;

        // 버튼 레이블 업데이트 + × 버튼 표시
        TcSelectBtn.Content   = $"{tc.Name}  ▾";
        TcClearBtn.Visibility = Visibility.Visible;

        // 커맨드가 있으면 MainViewModel이 양쪽 VM(ScenarioPacketListVM + PacketListVM) 동기화
        if (TcSelectedCommand?.CanExecute(tc) == true)
        {
            TcSelectedCommand.Execute(tc);
        }
        else if (DataContext is PacketListViewModel vm)
        {
            // 폴백: PacketListVM에만 로드 (ScenarioLab 동기화 없음)
            vm.LoadSequence(TestCaseSerializer.RestoreSequence(tc.Items));
        }
    }

    private static void DiagLog(string msg)
    {
        System.Diagnostics.Debug.WriteLine(msg);
        try { System.IO.File.AppendAllText(@"C:\Users\tht12\plv_diag.txt", msg + "\n"); } catch { }
    }

    // ── Send button Click handlers ────────────────────────────────────────────
    private void SendSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        DiagLog($"[PLV] SendSelected clicked  SendVM={SendVM?.GetHashCode().ToString() ?? "NULL"}  IsLoaded={IsLoaded}");
        if (SendVM == null) { DiagLog("[PLV] ABORT: SendVM is null"); return; }
        SendVM.ToggleSendSelectedDirect();
    }

    private void SendListBtn_Click(object sender, RoutedEventArgs e)
    {
        DiagLog($"[PLV] SendList clicked  SendVM={SendVM?.GetHashCode().ToString() ?? "NULL"}  IsLoaded={IsLoaded}");
        if (SendVM == null) { DiagLog("[PLV] ABORT: SendVM is null"); return; }
        SendVM.ToggleSendListDirect();
    }

    // ── Inline rename ─────────────────────────────────────────────────────────
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
        if (e.Key == Key.Enter)  { CommitEdit(editBox); e.Handled = true; }
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
