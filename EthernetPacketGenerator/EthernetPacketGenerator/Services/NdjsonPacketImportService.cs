using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Services;

public sealed record NdjsonPacketImportResult(
    int LineNumber,
    bool Ok,
    string Name,
    string InterfaceName,
    int Length,
    string Summary,
    string Error,
    PacketItem? Packet,
    int RepeatCount,
    int IntervalMs);

public sealed class NdjsonPacketImportService
{
    private static readonly string[] HexPropertyNames =
    [
        "frameHex", "packetHex", "rawHex", "dataHex", "hex", "frame", "bytes"
    ];

    public IReadOnlyList<NdjsonPacketImportResult> ParseLines(string ndjson)
    {
        var results = new List<NdjsonPacketImportResult>();
        var lines = ndjson.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            results.Add(ParseLine(line, i + 1));
        }

        return results;
    }

    private static NdjsonPacketImportResult ParseLine(string line, int lineNumber)
    {
        try
        {
            JsonObject? obj = null;
            if (line.StartsWith('{'))
                obj = JsonNode.Parse(line) as JsonObject;

            var hex = obj != null ? FindString(obj, HexPropertyNames) : line;
            if (string.IsNullOrWhiteSpace(hex))
                return Fail(lineNumber, "No frame hex field found.");

            var bytes = ParseHex(hex);
            if (bytes.Length < 14)
                return Fail(lineNumber, $"Frame is too short: {bytes.Length} bytes.");

            var packet = BuildRawEthernetPacket(bytes);
            packet.Name = FindString(obj, "name", "id", "label") ?? $"NDJSON-{lineNumber}";

            var iface = FindString(obj, "interface", "interfaceName", "iface", "nic") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(iface))
                packet.OutgoingInterfaceNames.Add(iface);

            var repeat = Math.Max(1, FindInt(obj, "count", "repeat", "repeatCount") ?? 1);
            var intervalMs = Math.Max(0, FindInt(obj, "intervalMs", "delayMs") ?? 0);
            var summary = $"{packet.SrcMac} -> {packet.DstMac} / {packet.ProtocolSummary}";

            return new NdjsonPacketImportResult(
                lineNumber,
                true,
                packet.Name,
                iface,
                bytes.Length,
                summary,
                string.Empty,
                packet,
                repeat,
                intervalMs);
        }
        catch (Exception ex)
        {
            return Fail(lineNumber, ex.Message);
        }
    }

    private static NdjsonPacketImportResult Fail(int lineNumber, string error) =>
        new(lineNumber, false, $"Line {lineNumber}", string.Empty, 0, string.Empty, error, null, 1, 0);

    private static PacketItem BuildRawEthernetPacket(byte[] bytes)
    {
        var packet = new PacketItem();

        var eth = new EthernetBlock();
        eth.ImportBytes(bytes, 0);
        packet.Blocks.Add(eth);

        if (bytes.Length > 14)
        {
            var payload = new RawPayloadBlock();
            payload.SetBytes(bytes.Skip(14).ToArray());
            packet.Blocks.Add(payload);
        }

        packet.Invalidate();
        return packet;
    }

    private static string? FindString(JsonObject? obj, params string[] names)
    {
        if (obj == null) return null;

        foreach (var name in names)
        {
            var found = FindProperty(obj, name);
            if (found == null) continue;

            if (found is JsonValue value)
            {
                if (value.TryGetValue<string>(out var text))
                    return text;
            }
        }

        return null;
    }

    private static int? FindInt(JsonObject? obj, params string[] names)
    {
        if (obj == null) return null;

        foreach (var name in names)
        {
            var found = FindProperty(obj, name);
            if (found == null) continue;

            if (found is JsonValue value)
            {
                if (value.TryGetValue<int>(out var number))
                    return number;
                if (value.TryGetValue<string>(out var text) && int.TryParse(text, out number))
                    return number;
            }
        }

        return null;
    }

    private static JsonNode? FindProperty(JsonNode node, string name)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;

                if (kv.Value != null)
                {
                    var nested = FindProperty(kv.Value, name);
                    if (nested != null) return nested;
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item == null) continue;
                var nested = FindProperty(item, name);
                if (nested != null) return nested;
            }
        }

        return null;
    }

    private static byte[] ParseHex(string text)
    {
        var matches = Regex.Matches(text, @"(?<![0-9A-Fa-f])(?:0x)?([0-9A-Fa-f]{2})(?![0-9A-Fa-f])");
        if (matches.Count > 0)
            return matches.Cast<Match>().Select(m => Convert.ToByte(m.Groups[1].Value, 16)).ToArray();

        var compact = Regex.Replace(text, @"[^0-9A-Fa-f]", "");
        if (compact.Length == 0)
            return [];
        if (compact.Length % 2 != 0)
            compact = "0" + compact;

        return Enumerable.Range(0, compact.Length / 2)
            .Select(i => Convert.ToByte(compact.Substring(i * 2, 2), 16))
            .ToArray();
    }
}
