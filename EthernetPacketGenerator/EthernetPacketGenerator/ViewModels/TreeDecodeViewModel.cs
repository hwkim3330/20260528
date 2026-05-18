using System.Collections.ObjectModel;
using System.ComponentModel;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels;

public class TreeDecodeViewModel : ViewModelBase
{
    private PacketItem? _currentPacket;

    public ObservableCollection<TreeNode> Roots { get; } = new();

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
                if (e.PropertyName == nameof(PacketItem.FullBytes) ||
                    e.PropertyName == nameof(PacketItem.ValidationResults))
                    Refresh();
            };
            packet.PropertyChanged += _packetHandler;
        }
        else
        {
            _packetHandler = null;
        }

        Refresh();
    }

    private bool _pinnedToCapture;

    // 캡처된 패킷 바이트로 트리 갱신 (패킷 에디터와 독립)
    public void DecodeCapture(byte[] data)
    {
        _pinnedToCapture = data.Length > 0;
        Decode(data, new List<ValidationResult>());
    }

    // 에디터 패킷으로 복귀
    public void UnpinCapture()
    {
        _pinnedToCapture = false;
        Refresh();
    }

    private void Refresh()
    {
        if (_pinnedToCapture) return;  // 캡처 선택 중엔 에디터 변경 무시
        var data = _currentPacket?.FullBytes ?? Array.Empty<byte>();
        var validations = _currentPacket?.ValidationResults ?? new List<ValidationResult>();
        Decode(data, validations);
    }

    public void Decode(byte[] data, IList<ValidationResult> validations)
    {
        Roots.Clear();
        if (data.Length == 0) return;

        var nodes = ProtocolDecoder.Decode(data);

        // Inject validation severity into root nodes by block index order
        // Root nodes correspond to top-level protocol blocks (Ethernet, then L3, then L4 nested)
        // ValidationResults carry a BlockIndex that maps to the packet's block list
        // We map: root node index 0 = block 0, root node index 1 = block 1, etc.
        // But ProtocolDecoder nests L4 inside L3, so roots may not align 1:1 with block indices.
        // We attach the worst severity from any matching validation to each root node.
        for (int i = 0; i < nodes.Count; i++)
        {
            var worstSeverity = validations
                .Where(v => v.BlockIndex == i)
                .Select(v => v.Severity)
                .DefaultIfEmpty(ValidationSeverity.None)
                .Max();

            if (worstSeverity != ValidationSeverity.None)
            {
                nodes[i].Severity = worstSeverity;
                // Append validation messages as child warning nodes
                foreach (var v in validations.Where(v => v.BlockIndex == i))
                {
                    nodes[i].Children.Insert(0, new TreeNode
                    {
                        Label = v.Severity == ValidationSeverity.Error ? "[ERROR]" : "[WARN]",
                        Value = v.Message,
                        ByteOffset = nodes[i].ByteOffset,
                        ByteLength = nodes[i].ByteLength,
                        Severity = v.Severity
                    });
                }
            }
        }

        // Also check for blocks that have no corresponding decoder node (e.g. ARP after IPv4)
        // These show up in validations but not as separate root nodes — add them as error roots
        var packet = _currentPacket;
        if (packet != null)
        {
            for (int i = nodes.Count; i < packet.Blocks.Count; i++)
            {
                var block = packet.Blocks[i];
                var worstSeverity = validations
                    .Where(v => v.BlockIndex == i)
                    .Select(v => v.Severity)
                    .DefaultIfEmpty(ValidationSeverity.None)
                    .Max();

                var orphanNode = new TreeNode
                {
                    Label = block.DisplayName,
                    Value = $"{block.ByteLength} bytes",
                    ByteOffset = block.StartOffset,
                    ByteLength = block.ByteLength,
                    Severity = worstSeverity != ValidationSeverity.None
                        ? worstSeverity
                        : ValidationSeverity.Warning
                };

                foreach (var v in validations.Where(v => v.BlockIndex == i))
                {
                    orphanNode.Children.Add(new TreeNode
                    {
                        Label = v.Severity == ValidationSeverity.Error ? "[ERROR]" : "[WARN]",
                        Value = v.Message,
                        ByteOffset = block.StartOffset,
                        ByteLength = block.ByteLength,
                        Severity = v.Severity
                    });
                }

                nodes.Add(orphanNode);
            }
        }

        foreach (var n in nodes)
            Roots.Add(n);
    }
}
