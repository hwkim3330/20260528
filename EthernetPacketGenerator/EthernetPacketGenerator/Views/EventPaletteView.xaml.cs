using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.ViewModels;

namespace EthernetPacketGenerator.Views;

public partial class EventPaletteView : UserControl
{
    // key: 비트마스크 (0b000001~0b100000), value: MAC
    private static readonly IReadOnlyDictionary<int, string> PortMacMap =
        new Dictionary<int, string>
        {
            { 0b000001, "9C:6B:00:49:3A:32" },
            { 0b000010, "C8:4D:44:25:2D:37" },
            { 0b000100, "A0:36:9F:A8:E4:A7" },
            { 0b001000, "A0:36:9F:A8:E4:A5" },
            { 0b010000, "A0:36:9F:A8:E4:A4" },
            { 0b100000, "A0:36:9F:A8:E4:A6" },
        };

    private static string PortDisplay(int bitmask) =>
        PortMacMap.TryGetValue(bitmask, out var mac)
            ? $"Port 0b{Convert.ToString(bitmask, 2).PadLeft(6, '0')}  ({mac})"
            : $"Port 0b{Convert.ToString(bitmask, 2).PadLeft(6, '0')}";

    public EventPaletteView()
    {
        InitializeComponent();
        // ComboBox 항목 초기화
        foreach (var kv in PortMacMap)
        {
            FdbPortCombo.Items.Add(new ComboBoxItem { Content = PortDisplay(kv.Key), Tag = kv.Key });
            FdbBucketPortCombo.Items.Add(new ComboBoxItem { Content = PortDisplay(kv.Key), Tag = kv.Key });
        }
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PacketListViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        CommitCurrentEdits();
        _editingEvent = null;
        if (e.NewValue is PacketListViewModel newVm)
            newVm.PropertyChanged += OnVmPropertyChanged;
        RefreshEditor();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PacketListViewModel.SelectedSequenceItem))
        {
            CommitCurrentEdits();
            RefreshEditor();
        }
    }

    // ── tile drag state ───────────────────────────────────────────────────────
    private SequenceEventType?   _pendingTileType;
    private Point                _tileMouseDownPos;
    private bool                 _tileDragging;
    private SeqItemAdornerWindow? _tileAdorner;

    private SequenceEvent? _editingEvent;

    private void CommitCurrentEdits()
    {
        var ev = _editingEvent;
        if (ev == null) return;
        switch (ev.EventType)
        {
            case SequenceEventType.Delay:
                if (int.TryParse(DelayMsBox.Text, out int d) && d >= 0) ev.DelayMs = d;
                break;
            case SequenceEventType.RegWrite:
                if (TryParseHex(RegAddressBox.Text, out uint a)) ev.Address = a;
                if (TryParseHex(RegValueBox.Text,   out uint v)) ev.Value   = v;
                break;
            case SequenceEventType.RegRead:
                if (TryParseHex(RegAddressBox.Text, out uint ra)) ev.Address = ra;
                break;
            case SequenceEventType.RegVerify:
                if (TryParseHex(WaitAddressBox.Text,  out uint wa)) ev.Address  = wa;
                if (TryParseHex(WaitMaskBox.Text,     out uint m))  ev.Mask     = m;
                if (TryParseHex(WaitExpectedBox.Text, out uint ex)) ev.Expected = ex;
                if (int.TryParse(WaitTimeoutBox.Text, out int t) && t > 0) ev.TimeoutMs = t;
                break;
            case SequenceEventType.FdbWrite:
                ev.MacAddress = FdbMacBox.Text;
                ev.VlanValid  = FdbVlanValidBox.IsChecked == true;
                if (int.TryParse(FdbVlanIdBox.Text, out int fvid)) ev.VlanId = fvid;
                // 직접 입력 우선, 없으면 ComboBox 값 사용
                if (int.TryParse(FdbPortBox.Text, out int fpDirect) && fpDirect > 0)
                    ev.Port = fpDirect;
                else if (FdbPortCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is int fp)
                    ev.Port = fp;
                break;
            case SequenceEventType.FdbWriteBucket:
                ev.MacAddress = FdbWMacBox.Text;
                if (FdbBucketPortCombo.SelectedItem is ComboBoxItem cbi2 && cbi2.Tag is int fp2) ev.Port = fp2;
                if (int.TryParse(FdbWBucketBox.Text, out int wb)) ev.Bucket = wb;
                if (TryParseHex(FdbWSlotBox.Text, out uint ws)) ev.SlotBitmap = (int)(ws & 0xF);
                break;
            case SequenceEventType.FdbRead:
                ev.MacAddress = FdbRHashMacBox.Text;
                ev.VlanValid  = FdbRHashVlanValidBox.IsChecked == true;
                if (int.TryParse(FdbRHashVlanIdBox.Text, out int rvid)) ev.VlanId = rvid;
                break;
            case SequenceEventType.FdbReadBucket:
                if (int.TryParse(FdbRBucketBox.Text, out int rb)) ev.Bucket = rb;
                if (TryParseHex(FdbRSlotBox.Text, out uint rs)) ev.SlotBitmap = (int)(rs & 0xF);
                ev.FdbExpectedMac = FdbRExpectedMacBox.Text.Trim();
                break;
            case SequenceEventType.RxVerify:
                ev.ExpectedDstMac = RxExpectedMacBox.Text.Trim();
                if (int.TryParse(RxTimeoutBox.Text, out int rxT) && rxT > 0) ev.TimeoutMs = rxT;
                break;
        }
    }

    // ── 편집 영역 갱신 ────────────────────────────────────────────────────────
    private void RefreshEditor()
    {
        var vm = DataContext as PacketListViewModel;
        var ev = vm?.SelectedSequenceItem?.Event;
        _editingEvent = ev;

        EventEditor.Visibility = ev != null ? Visibility.Visible : Visibility.Collapsed;
        if (ev == null) return;

        // 패널 전부 숨김
        DelayPanel.Visibility          = Visibility.Collapsed;
        RegPanel.Visibility            = Visibility.Collapsed;
        FdbReadHashPanel.Visibility    = Visibility.Collapsed;
        RegWaitPanel.Visibility        = Visibility.Collapsed;
        FdbWritePanel.Visibility       = Visibility.Collapsed;
        FdbWriteBucketPanel.Visibility = Visibility.Collapsed;
        FdbReadPanel.Visibility        = Visibility.Collapsed;
        RxVerifyPanel.Visibility       = Visibility.Collapsed;

        switch (ev.EventType)
        {
            case SequenceEventType.Delay:
                EditorTitle.Text       = "⏱  Delay 편집";
                DelayPanel.Visibility  = Visibility.Visible;
                DelayMsBox.Text        = ev.DelayMs.ToString();
                break;

            case SequenceEventType.RegWrite:
                EditorTitle.Text         = "✎  Reg Write 편집";
                RegPanel.Visibility      = Visibility.Visible;
                RegValuePanel.Visibility = Visibility.Visible;
                RegAddressBox.Text       = $"0x{ev.Address:X8}";
                RegValueBox.Text         = $"0x{ev.Value:X8}";
                break;

            case SequenceEventType.RegRead:
                EditorTitle.Text         = "⤷  Reg Read 편집";
                RegPanel.Visibility      = Visibility.Visible;
                RegValuePanel.Visibility = Visibility.Collapsed;
                RegAddressBox.Text       = $"0x{ev.Address:X8}";
                break;

            case SequenceEventType.RegVerify:
                EditorTitle.Text           = "✅  Reg Verify 편집";
                RegWaitPanel.Visibility    = Visibility.Visible;
                WaitAddressBox.Text        = $"0x{ev.Address:X8}";
                WaitMaskBox.Text           = $"0x{ev.Mask:X8}";
                WaitExpectedBox.Text       = $"0x{ev.Expected:X8}";
                WaitTimeoutBox.Text        = ev.TimeoutMs.ToString();
                break;

            case SequenceEventType.FdbWrite:
                EditorTitle.Text           = "📋  FDB Write 편집";
                FdbWritePanel.Visibility   = Visibility.Visible;
                FdbMacBox.Text             = ev.MacAddress;
                FdbVlanValidBox.IsChecked  = ev.VlanValid;
                FdbVlanIdBox.Text          = ev.VlanId.ToString();
                FdbPortBox.Text            = ev.Port > 0 ? ev.Port.ToString() : "";
                FdbPortCombo.SelectedItem  = FdbPortCombo.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is int t && t == ev.Port);
                break;

            case SequenceEventType.FdbWriteBucket:
                EditorTitle.Text               = "📋  FDB Write(Bucket) 편집";
                FdbWriteBucketPanel.Visibility  = Visibility.Visible;
                FdbWMacBox.Text                = ev.MacAddress;
                FdbWBucketBox.Text             = ev.Bucket.ToString();
                FdbWSlotBox.Text               = $"0x{ev.SlotBitmap:X}";
                FdbBucketPortCombo.SelectedItem = FdbBucketPortCombo.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is int t && t == ev.Port);
                break;

            case SequenceEventType.FdbRead:
                EditorTitle.Text               = "🔍  FDB Read 편집";
                FdbReadHashPanel.Visibility    = Visibility.Visible;
                FdbRHashMacBox.Text            = ev.MacAddress;
                FdbRHashVlanValidBox.IsChecked = ev.VlanValid;
                FdbRHashVlanIdBox.Text         = ev.VlanId.ToString();
                break;

            case SequenceEventType.FdbReadBucket:
                EditorTitle.Text                 = "🔍  FDB Read(Bucket) 편집";
                FdbReadPanel.Visibility          = Visibility.Visible;
                FdbRExpectedMacPanel.Visibility  = Visibility.Visible;
                FdbWaitTimeoutPanel.Visibility   = Visibility.Collapsed;
                FdbRBucketBox.Text               = ev.Bucket.ToString();
                FdbRSlotBox.Text                 = $"0x{ev.SlotBitmap:X}";
                FdbRExpectedMacBox.Text          = ev.FdbExpectedMac;
                break;

            case SequenceEventType.FdbFlush:
                EditorTitle.Text = "🗑  FDB Initialize — 파라미터 없음";
                break;

            case SequenceEventType.RxVerify:
                EditorTitle.Text         = "📥  RX Verify 편집";
                RxVerifyPanel.Visibility = Visibility.Visible;
                RxExpectedMacBox.Text    = ev.ExpectedDstMac;
                RxTimeoutBox.Text        = ev.TimeoutMs.ToString();
                break;
        }
    }

    // ── 타일 마우스 이벤트 (클릭 = 삽입, 드래그 = PacketListView에 드롭) ────────
    private void DelayTile_Click(object s, MouseButtonEventArgs e)          => TileMouseDown(s, e, SequenceEventType.Delay);
    private void RegWriteTile_Click(object s, MouseButtonEventArgs e)       => TileMouseDown(s, e, SequenceEventType.RegWrite);
    private void RegReadTile_Click(object s, MouseButtonEventArgs e)        => TileMouseDown(s, e, SequenceEventType.RegRead);
    private void VerifyTile_Click(object s, MouseButtonEventArgs e)         => TileMouseDown(s, e, SequenceEventType.RegVerify);
    private void FdbWriteTile_Click(object s, MouseButtonEventArgs e)       => TileMouseDown(s, e, SequenceEventType.FdbWrite);
    private void FdbWriteBucketTile_Click(object s, MouseButtonEventArgs e) => TileMouseDown(s, e, SequenceEventType.FdbWriteBucket);
    private void FdbReadTile_Click(object s, MouseButtonEventArgs e)        => TileMouseDown(s, e, SequenceEventType.FdbRead);
    private void FdbReadBucketTile_Click(object s, MouseButtonEventArgs e)  => TileMouseDown(s, e, SequenceEventType.FdbReadBucket);
    private void FdbFlushTile_Click(object s, MouseButtonEventArgs e)       => TileMouseDown(s, e, SequenceEventType.FdbFlush);
    private void RxVerifyTile_Click(object s, MouseButtonEventArgs e)       => TileMouseDown(s, e, SequenceEventType.RxVerify);

    private void TileMouseDown(object sender, MouseButtonEventArgs e, SequenceEventType type)
    {
        _pendingTileType  = type;
        _tileMouseDownPos = e.GetPosition(this);
        _tileDragging     = false;
        if (sender is UIElement el) el.CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_pendingTileType == null && !_tileDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { FinishTileDrag(commit: false); return; }

        if (!_tileDragging)
        {
            var pos = e.GetPosition(this);
            bool over =
                Math.Abs(pos.X - _tileMouseDownPos.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - _tileMouseDownPos.Y) > SystemParameters.MinimumVerticalDragDistance;
            if (!over) return;
            _tileDragging = true;

            // 어도너 창 생성
            _tileAdorner = CreateEventAdorner(_pendingTileType!.Value);
            _tileAdorner?.Show();
        }

        _tileAdorner?.PlaceAtCursor(new Point(60, 12));

        // notify PacketListView of cursor position (screen pixels)
        DragAdornerWindow.GetCursorScreenPx(out int sx, out int sy);
        PacketListView.ActiveDropTarget?.PaletteDragOver(_pendingTileType!.Value, new Point(sx, sy));
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_pendingTileType == null && !_tileDragging) return;

        if (_tileDragging)
        {
            DragAdornerWindow.GetCursorScreenPx(out int sx, out int sy);
            PacketListView.ActiveDropTarget?.PaletteDrop(_pendingTileType!.Value, new Point(sx, sy));
            FinishTileDrag(commit: false);
        }
        else if (_pendingTileType.HasValue)
        {
            // plain click — insert via VM command
            if (DataContext is PacketListViewModel vm)
            {
                switch (_pendingTileType.Value)
                {
                    case SequenceEventType.Delay:          vm.AddDelayEventCommand.Execute(null);     break;
                    case SequenceEventType.RegWrite:        vm.AddRegWriteCommand.Execute(null);       break;
                    case SequenceEventType.RegRead:         vm.AddRegReadCommand.Execute(null);        break;
                    case SequenceEventType.RegVerify:          vm.AddRegVerifyCommand.Execute(null);      break;
                    case SequenceEventType.FdbWrite:        vm.AddFdbWriteCommand.Execute(null);       break;
                    case SequenceEventType.FdbWriteBucket:  vm.AddFdbWriteBucketCommand.Execute(null); break;
                    case SequenceEventType.FdbRead:         vm.AddFdbReadCommand.Execute(null);        break;
                    case SequenceEventType.FdbReadBucket:   vm.AddFdbReadBucketCommand.Execute(null);  break;
                    case SequenceEventType.FdbFlush:        vm.AddFdbFlushCommand.Execute(null);       break;
                    case SequenceEventType.RxVerify:        vm.AddRxVerifyCommand.Execute(null);       break;
                }
            }
            FinishTileDrag(commit: false);
        }
        e.Handled = true;
    }

    private void FinishTileDrag(bool commit)
    {
        PacketListView.ActiveDropTarget?.PaletteDragLeave();
        // release capture from whichever tile element holds it
        if (Mouse.Captured is UIElement cap) cap.ReleaseMouseCapture();
        _tileAdorner?.Close();
        _tileAdorner     = null;
        _pendingTileType = null;
        _tileDragging    = false;
    }

    private static SeqItemAdornerWindow? CreateEventAdorner(SequenceEventType type)
    {
        try
        {
            var label = type switch
            {
                SequenceEventType.Delay          => "⏱  Delay",
                SequenceEventType.RegWrite        => "✎  Reg Write",
                SequenceEventType.RegRead         => "⤷  Reg Read",
                SequenceEventType.RegVerify          => "✅  Reg Verify",
                SequenceEventType.FdbWrite        => "📋  FDB Write",
                SequenceEventType.FdbWriteBucket  => "📋  FDB Write(Bucket)",
                SequenceEventType.FdbRead         => "🔍  FDB Read",
                SequenceEventType.FdbReadBucket   => "🔍  FDB Read(Bucket)",
                SequenceEventType.FdbFlush        => "🗑  FDB Initialize",
                SequenceEventType.RxVerify        => "📥  RX Verify",
                _                                 => type.ToString()
            };
            var ev = new EthernetPacketGenerator.Models.SequenceEvent { EventType = type };
            var si = new EthernetPacketGenerator.Models.SequenceItem(ev);
            return SeqItemAdornerWindow.Create(si);
        }
        catch { return null; }
    }

    // ── 편집 필드 → 모델 반영 ────────────────────────────────────────────────
    private SequenceEvent? Ev => (DataContext as PacketListViewModel)?.SelectedSequenceItem?.Event;

    private void DelayMsBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(DelayMsBox.Text, out int v) && v >= 0) ev.DelayMs = v; }

    private void RegAddressBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && TryParseHex(RegAddressBox.Text, out uint v)) ev.Address = v; }

    private void RegValueBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && TryParseHex(RegValueBox.Text, out uint v)) ev.Value = v; }

    private void FdbRHashMacBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.MacAddress = FdbRHashMacBox.Text; }

    private void FdbRHashVlanValid_Changed(object s, RoutedEventArgs e)
    { if (Ev is { } ev) ev.VlanValid = FdbRHashVlanValidBox.IsChecked == true; }

    private void FdbRHashVlanIdBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(FdbRHashVlanIdBox.Text, out int v)) ev.VlanId = v; }

    private void WaitAddressBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && TryParseHex(WaitAddressBox.Text, out uint v)) ev.Address = v; }

    private void WaitMaskBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && TryParseHex(WaitMaskBox.Text, out uint v)) ev.Mask = v; }

    private void WaitExpectedBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && TryParseHex(WaitExpectedBox.Text, out uint v)) ev.Expected = v; }

    private void WaitTimeoutBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(WaitTimeoutBox.Text, out int v) && v > 0) ev.TimeoutMs = v; }

    private void FdbVlanValid_Changed(object s, RoutedEventArgs e)
    { if (Ev is { } ev) ev.VlanValid = FdbVlanValidBox.IsChecked == true; }

    private void FdbVlanIdBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(FdbVlanIdBox.Text, out int v)) ev.VlanId = v; }

    private void FdbPortCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (Ev is not { } ev) return;
        if (FdbPortCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int port) return;
        ev.Port        = port;
        ev.MacAddress  = PortMacMap.TryGetValue(port, out var mac) ? mac : ev.MacAddress;
        // 직접 입력 박스에도 동기화 (TextChanged가 재진입해도 값이 같으므로 무해)
        if (FdbPortBox.Text != port.ToString()) FdbPortBox.Text = port.ToString();
        FdbMacBox.Text = ev.MacAddress;
    }

    private void FdbPortBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (Ev is not { } ev) return;
        if (!int.TryParse(FdbPortBox.Text, out int port) || port <= 0) return;
        ev.Port = port;
        // 알려진 포트면 ComboBox 동기화 + MAC 자동 입력
        var match = FdbPortCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int t && t == port);
        if (match != null && FdbPortCombo.SelectedItem != match) FdbPortCombo.SelectedItem = match;
        if (PortMacMap.TryGetValue(port, out var mac) && string.IsNullOrEmpty(FdbMacBox.Text))
            FdbMacBox.Text = mac;
    }

    private void FdbMacBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.MacAddress = FdbMacBox.Text; }

    private void FdbRBucketBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(FdbRBucketBox.Text, out int v) && v is >= 0 and <= 1023) ev.Bucket = v; }

    private void FdbRSlotBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && TryParseHex(FdbRSlotBox.Text, out uint v)) ev.SlotBitmap = (int)(v & 0xF); }

    private void FdbRExpectedMacBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.FdbExpectedMac = FdbRExpectedMacBox.Text.Trim(); }

    private void FdbRTimeoutBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(FdbRTimeoutBox.Text, out int v) && v > 0) ev.TimeoutMs = v; }

    private void FdbBucketPortCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (Ev is not { } ev) return;
        if (FdbBucketPortCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int port) return;
        ev.Port       = port;
        ev.MacAddress = PortMacMap.TryGetValue(port, out var mac) ? mac : ev.MacAddress;
        FdbWMacBox.Text = ev.MacAddress;
    }

    private void FdbWMacBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.MacAddress = FdbWMacBox.Text; }

    private void FdbWBucketBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(FdbWBucketBox.Text, out int v) && v is >= 0 and <= 1023) ev.Bucket = v; }

    private void FdbWSlotBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && TryParseHex(FdbWSlotBox.Text, out uint v)) ev.SlotBitmap = (int)(v & 0xF); }

    private void RxExpectedMacBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.ExpectedDstMac = RxExpectedMacBox.Text.Trim(); }

    private void RxTimeoutBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(RxTimeoutBox.Text, out int v) && v > 0) ev.TimeoutMs = v; }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────
    private static bool TryParseHex(string text, out uint result)
    {
        var clean = text.Replace("0x", "").Replace("0X", "").Replace("_", "").Trim();
        return uint.TryParse(clean, NumberStyles.HexNumber, null, out result);
    }
}
