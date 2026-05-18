using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels.RegisterViewer;

/// <summary>단일 비트/채널의 인터럽트 상태 표현</summary>
public class IntrBitItem : ViewModelBase
{
    private bool _active;
    public string Label    { get; }
    public bool   IsActive { get => _active; set => SetProperty(ref _active, value); }
    public IntrBitItem(string label) => Label = label;
}

public class InterruptViewModel : ViewModelBase
{
    private readonly RegisterService _reg;

    private const uint OFF_INTR_CTRL = 0x010;
    private const uint OFF_RAW_INTR  = 0x014;
    private const uint OFF_INTR_MASK = 0x018;
    private const uint OFF_INTR_SW   = 0x01C;

    // ── INTERRUPT_CTRL ───────────────────────────────────────────────────────
    private bool _intrActiveLow;
    public bool IntrActiveLow
    {
        get => _intrActiveLow;
        set => SetProperty(ref _intrActiveLow, value);
    }

    // ── RAW_INTERRUPT — 상태 표시용 컬렉션 ───────────────────────────────────
    public ObservableCollection<IntrBitItem> PortIntrBits { get; } = new();
    public ObservableCollection<IntrBitItem> MdioIntrBits { get; } = new();
    private bool _swIntrActive;
    public bool SwIntrActive { get => _swIntrActive; set => SetProperty(ref _swIntrActive, value); }

    // ── INTERRUPT_MASK — 체크박스용 컬렉션 ───────────────────────────────────
    public ObservableCollection<IntrBitItem> PortMaskBits { get; } = new();
    public ObservableCollection<IntrBitItem> MdioMaskBits { get; } = new();
    private bool _swMasked;
    public bool SwMasked { get => _swMasked; set => SetProperty(ref _swMasked, value); }

    // ── SW Trigger flash ─────────────────────────────────────────────────────
    private bool _isSwTriggered;
    public bool IsSwTriggered { get => _isSwTriggered; set => SetProperty(ref _isSwTriggered, value); }

    // ── Live Poll ────────────────────────────────────────────────────────────
    private DispatcherTimer? _pollTimer;
    private bool _isPolling;
    public bool IsPolling
    {
        get => _isPolling;
        set => SetProperty(ref _isPolling, value);
    }

    // ── Status ───────────────────────────────────────────────────────────────
    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand ReadAllCommand        { get; }
    public ICommand ReadCtrlCommand      { get; }
    public ICommand ApplyCtrlCommand     { get; }
    public ICommand ReadRawIntrCommand   { get; }
    public ICommand ReadMaskCommand      { get; }
    public ICommand ApplyMaskCommand     { get; }
    public ICommand TriggerSwIntrCommand { get; }
    public ICommand TogglePollCommand    { get; }

    public InterruptViewModel(RegisterService reg)
    {
        _reg = reg;

        for (int i = 0; i < 16; i++) PortIntrBits.Add(new IntrBitItem($"PORT{i}"));
        for (int i = 0; i < 8;  i++) MdioIntrBits.Add(new IntrBitItem($"MDIO{i}"));
        for (int i = 0; i < 16; i++) PortMaskBits.Add(new IntrBitItem($"PORT{i}"));
        for (int i = 0; i < 8;  i++) MdioMaskBits.Add(new IntrBitItem($"MDIO{i}"));

        ReadAllCommand       = new RelayCommand(async () => { await ReadCtrlAsync(); await ReadRawIntrAsync(); await ReadMaskAsync(); if (!_isPolling) TogglePoll(); });
        ReadCtrlCommand      = new RelayCommand(async () => await ReadCtrlAsync());
        ApplyCtrlCommand     = new RelayCommand(async () => await ApplyCtrlAsync());
        ReadRawIntrCommand   = new RelayCommand(async () => await ReadRawIntrAsync());
        ReadMaskCommand      = new RelayCommand(async () => await ReadMaskAsync());
        ApplyMaskCommand     = new RelayCommand(async () => await ApplyMaskAsync());
        TriggerSwIntrCommand = new RelayCommand(async () => await TriggerSwAsync());
        TogglePollCommand    = new RelayCommand(TogglePoll);
    }

    private void TogglePoll()
    {
        if (_isPolling)
        {
            _pollTimer?.Stop();
            _pollTimer = null;
            IsPolling = false;
            Status = string.Empty;
        }
        else
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += async (_, _) => await ReadRawIntrAsync();
            _pollTimer.Start();
            IsPolling = true;
            _ = ReadRawIntrAsync();
        }
    }

    private async Task ReadCtrlAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_INTR_CTRL);
            IntrActiveLow = (v & 0x1) != 0;
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ApplyCtrlAsync()
    {
        try
        {
            await _reg.WriteAsync(OFF_INTR_CTRL, IntrActiveLow ? 1u : 0u);
            Status = "극성 설정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ReadRawIntrAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_RAW_INTR);
            for (int i = 0; i < 16; i++)
                PortIntrBits[i].IsActive = ((v >> i) & 1) != 0;
            for (int i = 0; i < 8; i++)
                MdioIntrBits[i].IsActive = ((v >> (16 + i)) & 1) != 0;
            SwIntrActive = ((v >> 31) & 1) != 0;
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ReadMaskAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_INTR_MASK);
            for (int i = 0; i < 16; i++)
                PortMaskBits[i].IsActive = ((v >> i) & 1) != 0;
            for (int i = 0; i < 8; i++)
                MdioMaskBits[i].IsActive = ((v >> (16 + i)) & 1) != 0;
            SwMasked = ((v >> 31) & 1) != 0;
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ApplyMaskAsync()
    {
        try
        {
            uint v = 0;
            for (int i = 0; i < 16; i++)
                if (PortMaskBits[i].IsActive) v |= (1u << i);
            for (int i = 0; i < 8; i++)
                if (MdioMaskBits[i].IsActive) v |= (1u << (16 + i));
            if (SwMasked) v |= 0x8000_0000;
            await _reg.WriteAsync(OFF_INTR_MASK, v);
            Status = "마스크 설정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task TriggerSwAsync()
    {
        try
        {
            await _reg.WriteAsync(OFF_INTR_SW, 0x1);
            IsSwTriggered = true;
            await Task.Delay(600);
            IsSwTriggered = false;
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }
}
