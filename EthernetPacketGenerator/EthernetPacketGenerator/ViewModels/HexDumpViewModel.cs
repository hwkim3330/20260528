using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.ViewModels;

public class HexCell : ViewModelBase
{
    private byte _value;
    private bool _isHighlighted;
    private bool _isSelected;

    public int Offset { get; init; }

    public byte Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string HexText
    {
        get => _value.ToString("X2");
        set
        {
            if (byte.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                Value = b;
        }
    }

    public char AsciiChar => _value >= 0x20 && _value < 0x7F ? (char)_value : '.';

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public class HexRow : ViewModelBase
{
    public int RowOffset { get; init; }
    public string OffsetText => $"{RowOffset:X4}";
    public ObservableCollection<HexCell> Cells { get; } = new();

    public string AsciiText => new string(Cells.Select(c => c.AsciiChar).ToArray());

    public void NotifyAsciiChanged() => OnPropertyChanged(nameof(AsciiText));
}

public class HexDumpViewModel : ViewModelBase
{
    private PacketItem? _currentPacket;
    private ProtocolBlock? _highlightedBlock;
    private bool _isUpdating;

    public ObservableCollection<HexRow> Rows { get; } = new();
    public ICommand EditByteCommand { get; }

    public HexDumpViewModel()
    {
        EditByteCommand = new RelayCommand<(int offset, byte value)>(tuple =>
            EditByte(tuple.offset, tuple.value));
    }

    private PropertyChangedEventHandler? _packetHandler;

    public void SetPacket(PacketItem? packet)
    {
        if (_currentPacket != null && _packetHandler != null)
            _currentPacket.PropertyChanged -= _packetHandler;

        _currentPacket = packet;

        if (packet != null)
        {
            _packetHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(PacketItem.FullBytes) && !_isUpdating)
                    RefreshRows(packet.FullBytes);
            };
            packet.PropertyChanged += _packetHandler;
        }
        else
        {
            _packetHandler = null;
        }

        RefreshRows(packet?.FullBytes ?? Array.Empty<byte>());
    }

    public void SetHighlightedBlock(ProtocolBlock? block)
    {
        _highlightedBlock = block;
        ApplyHighlights();
    }

    public void RefreshRows(byte[] data)
    {
        Rows.Clear();
        int rowCount = (data.Length + 15) / 16;

        for (int r = 0; r < rowCount; r++)
        {
            var row = new HexRow { RowOffset = r * 16 };
            for (int c = 0; c < 16; c++)
            {
                int offset = r * 16 + c;
                byte val = offset < data.Length ? data[offset] : (byte)0;
                bool valid = offset < data.Length;
                var cell = new HexCell { Offset = offset, Value = val };
                if (!valid) cell.IsHighlighted = false;
                row.Cells.Add(cell);
            }
            Rows.Add(row);
        }

        ApplyHighlights();
    }

    private void ApplyHighlights()
    {
        if (_highlightedBlock == null) return;

        int start = _highlightedBlock.StartOffset;
        int end = start + _highlightedBlock.ByteLength;

        foreach (var row in Rows)
            foreach (var cell in row.Cells)
                cell.IsHighlighted = cell.Offset >= start && cell.Offset < end;
    }

    public void EditByte(int offset, byte newValue)
    {
        if (_currentPacket == null) return;
        _isUpdating = true;
        try
        {
            var bytes = (byte[])_currentPacket.FullBytes.Clone();
            if (offset >= bytes.Length) return;
            bytes[offset] = newValue;
            _currentPacket.ImportFromBytes(bytes);

            // Update the specific cell visually
            int row = offset / 16;
            int col = offset % 16;
            if (row < Rows.Count && col < Rows[row].Cells.Count)
            {
                Rows[row].Cells[col].Value = newValue;
                Rows[row].NotifyAsciiChanged();
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }
}
