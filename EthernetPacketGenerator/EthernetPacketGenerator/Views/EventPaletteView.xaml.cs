using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.ViewModels;

namespace EthernetPacketGenerator.Views;

public partial class EventPaletteView : UserControl
{
    public EventPaletteView()
    {
        InitializeComponent();
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
            case SequenceEventType.RegWaitFor:
            case SequenceEventType.FdbWaitFor:
                if (TryParseHex(WaitAddressBox.Text,  out uint wa)) ev.Address  = wa;
                if (TryParseHex(WaitMaskBox.Text,     out uint m))  ev.Mask     = m;
                if (TryParseHex(WaitExpectedBox.Text, out uint ex)) ev.Expected = ex;
                if (int.TryParse(WaitTimeoutBox.Text, out int t) && t > 0) ev.TimeoutMs = t;
                break;
            case SequenceEventType.FdbWrite:
                ev.MacAddress = FdbMacBox.Text;
                ev.VlanValid  = FdbVlanValidBox.IsChecked == true;
                if (int.TryParse(FdbVlanIdBox.Text, out int fvid)) ev.VlanId = fvid;
                if (int.TryParse(FdbPortBox.Text,   out int fp))   ev.Port   = fp;
                break;
            case SequenceEventType.FdbRead:
                ev.MacAddress = FdbRHashMacBox.Text;
                ev.VlanValid  = FdbRHashVlanValidBox.IsChecked == true;
                if (int.TryParse(FdbRHashVlanIdBox.Text, out int rvid)) ev.VlanId = rvid;
                break;
            case SequenceEventType.CaptureVerify:
                ev.CaptureInterface = CaptureIfaceBox.Text;
                ev.CaptureFilter    = CaptureFilterBox.Text;
                if (int.TryParse(CaptureExpectedBox.Text, out int cexp) && cexp > 0) ev.CaptureExpected = cexp;
                if (int.TryParse(CaptureTimeoutBox.Text,  out int ctms) && ctms > 0) ev.TimeoutMs        = ctms;
                break;
            case SequenceEventType.SerialSend:
                ev.SerialText = SerialSendTextBox.Text;
                ev.SerialHex  = SerialSendHexBox.Text;
                break;
            case SequenceEventType.SerialVerify:
                ev.SerialText = SerialVerifyTextBox.Text;
                if (int.TryParse(SerialVerifyTimeoutBox.Text, out int svtms) && svtms > 0) ev.TimeoutMs = svtms;
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
        DelayPanel.Visibility        = Visibility.Collapsed;
        RegPanel.Visibility          = Visibility.Collapsed;
        FdbReadHashPanel.Visibility  = Visibility.Collapsed;
        RegWaitPanel.Visibility      = Visibility.Collapsed;
        FdbWritePanel.Visibility     = Visibility.Collapsed;
        FdbReadPanel.Visibility      = Visibility.Collapsed;
        CaptureVerifyPanel.Visibility = Visibility.Collapsed;
        SerialSendPanel.Visibility    = Visibility.Collapsed;
        SerialVerifyPanel.Visibility  = Visibility.Collapsed;

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

            case SequenceEventType.FdbWrite:
                EditorTitle.Text           = "📋  FDB Write 편집";
                FdbWritePanel.Visibility   = Visibility.Visible;
                FdbMacBox.Text             = ev.MacAddress;
                FdbVlanValidBox.IsChecked  = ev.VlanValid;
                FdbVlanIdBox.Text          = ev.VlanId.ToString();
                FdbPortBox.Text            = ev.Port.ToString();
                break;

            case SequenceEventType.FdbRead:
                EditorTitle.Text              = "🔍  FDB Read 편집";
                FdbReadHashPanel.Visibility   = Visibility.Visible;
                FdbRHashMacBox.Text           = ev.MacAddress;
                FdbRHashVlanValidBox.IsChecked = ev.VlanValid;
                FdbRHashVlanIdBox.Text        = ev.VlanId.ToString();
                break;

            case SequenceEventType.RegWaitFor:
                EditorTitle.Text           = "⏳  Reg WaitFor 편집";
                RegWaitPanel.Visibility    = Visibility.Visible;
                WaitAddressBox.Text        = $"0x{ev.Address:X8}";
                WaitMaskBox.Text           = $"0x{ev.Mask:X8}";
                WaitExpectedBox.Text       = $"0x{ev.Expected:X8}";
                WaitTimeoutBox.Text        = ev.TimeoutMs.ToString();
                break;

            case SequenceEventType.FdbWaitFor:
                EditorTitle.Text           = "⏳  FDB WaitFor 편집";
                RegWaitPanel.Visibility    = Visibility.Visible;
                WaitAddressBox.Text        = $"0x{ev.Address:X8}";
                WaitMaskBox.Text           = $"0x{ev.Mask:X8}";
                WaitExpectedBox.Text       = $"0x{ev.Expected:X8}";
                WaitTimeoutBox.Text        = ev.TimeoutMs.ToString();
                break;


            case SequenceEventType.FdbFlush:
                EditorTitle.Text = "🗑  FDB Flush — 파라미터 없음";
                break;

            case SequenceEventType.CaptureVerify:
                EditorTitle.Text              = "📡  Capture Verify 편집";
                CaptureVerifyPanel.Visibility = Visibility.Visible;
                CaptureIfaceBox.Text          = ev.CaptureInterface;
                CaptureFilterBox.Text         = ev.CaptureFilter;
                CaptureExpectedBox.Text       = ev.CaptureExpected.ToString();
                CaptureTimeoutBox.Text        = ev.TimeoutMs.ToString();
                break;

            case SequenceEventType.SerialSend:
                EditorTitle.Text           = "→  Serial Send 편집";
                SerialSendPanel.Visibility = Visibility.Visible;
                SerialSendTextBox.Text     = ev.SerialText;
                SerialSendHexBox.Text      = ev.SerialHex;
                break;

            case SequenceEventType.SerialVerify:
                EditorTitle.Text             = "⏳  Serial Verify 편집";
                SerialVerifyPanel.Visibility = Visibility.Visible;
                SerialVerifyTextBox.Text     = ev.SerialText;
                SerialVerifyTimeoutBox.Text  = ev.TimeoutMs.ToString();
                break;
        }
    }

    // ── 타일 클릭 ─────────────────────────────────────────────────────────────
    private void DelayTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddDelayEventCommand.Execute(null); }

    private void RegWriteTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddRegWriteCommand.Execute(null); }

    private void RegReadTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddRegReadCommand.Execute(null); }

    private void RegWaitForTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddRegWaitForCommand.Execute(null); }

    private void FdbWriteTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddFdbWriteCommand.Execute(null); }

    private void FdbReadTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddFdbReadCommand.Execute(null); }

    private void FdbWaitForTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddFdbWaitForCommand.Execute(null); }

    private void FdbFlushTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddFdbFlushCommand.Execute(null); }

    private void CaptureVerifyTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddCaptureVerifyCommand.Execute(null); }

    private void SerialSendTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddSerialSendCommand.Execute(null); }

    private void SerialVerifyTile_Click(object s, MouseButtonEventArgs e)
    { if (DataContext is PacketListViewModel vm) vm.AddSerialVerifyCommand.Execute(null); }

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

    private void FdbMacBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.MacAddress = FdbMacBox.Text; }

    private void FdbVlanValid_Changed(object s, RoutedEventArgs e)
    { if (Ev is { } ev) ev.VlanValid = FdbVlanValidBox.IsChecked == true; }

    private void FdbVlanIdBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(FdbVlanIdBox.Text, out int v)) ev.VlanId = v; }

    private void FdbPortBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(FdbPortBox.Text, out int v)) ev.Port = v; }

    private void FdbRBucketBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(FdbRBucketBox.Text, out int v) && v is >= 0 and <= 1023) ev.Bucket = v; }

    private void FdbRSlotBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && TryParseHex(FdbRSlotBox.Text, out uint v)) ev.SlotBitmap = (int)(v & 0xF); }

    private void FdbRTimeoutBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(FdbRTimeoutBox.Text, out int v) && v > 0) ev.TimeoutMs = v; }

    private void CaptureIfaceBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.CaptureInterface = CaptureIfaceBox.Text; }

    private void CaptureFilterBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.CaptureFilter = CaptureFilterBox.Text; }

    private void CaptureExpectedBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(CaptureExpectedBox.Text, out int v) && v > 0) ev.CaptureExpected = v; }

    private void CaptureTimeoutBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(CaptureTimeoutBox.Text, out int v) && v > 0) ev.TimeoutMs = v; }

    private void SerialSendTextBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.SerialText = SerialSendTextBox.Text; }

    private void SerialSendHexBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.SerialHex = SerialSendHexBox.Text; }

    private void SerialVerifyTextBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev) ev.SerialText = SerialVerifyTextBox.Text; }

    private void SerialVerifyTimeoutBox_TextChanged(object s, TextChangedEventArgs e)
    { if (Ev is { } ev && int.TryParse(SerialVerifyTimeoutBox.Text, out int v) && v > 0) ev.TimeoutMs = v; }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────
    private static bool TryParseHex(string text, out uint result)
    {
        var clean = text.Replace("0x", "").Replace("0X", "").Replace("_", "").Trim();
        return uint.TryParse(clean, NumberStyles.HexNumber, null, out result);
    }
}
