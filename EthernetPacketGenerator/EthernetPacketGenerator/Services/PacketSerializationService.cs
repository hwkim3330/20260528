using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Services;

public static class PacketSerializationService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new ProtocolBlockConverter() }
    };

    public static void Save(IEnumerable<PacketItem> packets, string filePath)
    {
        var dtos = packets.Select(p => new PacketDto
        {
            Name = p.Name,
            Blocks = p.Blocks.Select(b => new BlockDto
            {
                Type = b.Type.ToString(),
                Bytes = Convert.ToBase64String(b.Bytes)
            }).ToList()
        }).ToList();

        var json = JsonSerializer.Serialize(dtos, Options);
        File.WriteAllText(filePath, json);
    }

    public static List<PacketItem> Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var dtos = JsonSerializer.Deserialize<List<PacketDto>>(json, Options)
                   ?? new List<PacketDto>();

        var packets = new List<PacketItem>();
        foreach (var dto in dtos)
        {
            var packet = new PacketItem { Name = dto.Name };
            foreach (var blockDto in dto.Blocks)
            {
                if (!Enum.TryParse<ProtocolType>(blockDto.Type, out var type))
                    continue;

                var block = PacketItem.CreateBlock(type);
                var bytes = Convert.FromBase64String(blockDto.Bytes);
                block.ImportBytes(bytes, 0);
                block.PropertyChanged += (_, _) => packet.Invalidate();
                packet.Blocks.Add(block);
            }
            packets.Add(packet);
        }
        return packets;
    }

    private class PacketDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Packet";

        [JsonPropertyName("blocks")]
        public List<BlockDto> Blocks { get; set; } = new();
    }

    private class BlockDto
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("bytes")]
        public string Bytes { get; set; } = string.Empty;
    }

    private class ProtocolBlockConverter : JsonConverter<ProtocolBlock>
    {
        public override ProtocolBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotImplementedException();

        public override void Write(Utf8JsonWriter writer, ProtocolBlock value, JsonSerializerOptions options)
            => throw new NotImplementedException();
    }
}
