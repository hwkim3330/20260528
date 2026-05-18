using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Services;

public static class TestCaseSerializer
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    // ── Snapshot: SequenceItem list → DTO list ────────────────────────────────
    public static List<SequenceItemDto> TakeSnapshot(IEnumerable<SequenceItem> items)
    {
        var dtos = new List<SequenceItemDto>();
        foreach (var item in items)
        {
            if (item.Kind == SequenceItemKind.Packet && item.Packet != null)
            {
                dtos.Add(new SequenceItemDto
                {
                    Kind       = "Packet",
                    IsChecked  = item.IsChecked,
                    PacketName = item.Packet.Name,
                    Blocks     = item.Packet.Blocks.Select(b => new SequenceItemDto.BlockDto
                    {
                        Type  = b.Type.ToString(),
                        Bytes = Convert.ToBase64String(b.Bytes)
                    }).ToList()
                });
            }
            else if (item.Kind == SequenceItemKind.Event && item.Event != null)
            {
                var ev = item.Event;
                dtos.Add(new SequenceItemDto
                {
                    Kind       = "Event",
                    EventType  = ev.EventType.ToString(),
                    DelayMs    = ev.DelayMs,
                    Address    = $"0x{ev.Address:X8}",
                    Value      = $"0x{ev.Value:X8}",
                    Mask       = $"0x{ev.Mask:X8}",
                    Expected   = $"0x{ev.Expected:X8}",
                    TimeoutMs  = ev.TimeoutMs,
                    MacAddress = ev.MacAddress,
                    VlanValid  = ev.VlanValid,
                    VlanId     = ev.VlanId,
                    Port       = ev.Port,
                    Bucket            = ev.Bucket,
                    SlotBitmap        = ev.SlotBitmap,
                    CaptureInterface  = ev.CaptureInterface,
                    CaptureFilter     = ev.CaptureFilter,
                    CaptureExpected   = ev.CaptureExpected,
                    SerialText        = ev.SerialText,
                    SerialHex         = ev.SerialHex
                });
            }
        }
        return dtos;
    }

    // ── Restore: DTO list → SequenceItem list ────────────────────────────────
    public static List<SequenceItem> RestoreSequence(List<SequenceItemDto> dtos)
    {
        var items = new List<SequenceItem>();
        foreach (var dto in dtos)
        {
            if (dto.Kind == "Packet")
            {
                var packet = new PacketItem { Name = dto.PacketName ?? "Packet" };
                foreach (var bd in dto.Blocks ?? new())
                {
                    if (!Enum.TryParse<ProtocolType>(bd.Type, out var type)) continue;
                    var block = PacketItem.CreateBlock(type);
                    block.ImportBytes(Convert.FromBase64String(bd.Bytes), 0);
                    block.PropertyChanged += (_, _) => packet.Invalidate();
                    packet.Blocks.Add(block);
                }
                var si = new SequenceItem(packet);
                si.IsChecked = dto.IsChecked;
                items.Add(si);
            }
            else if (dto.Kind == "Event")
            {
                if (!Enum.TryParse<SequenceEventType>(dto.EventType ?? "", out var evType)) continue;
                var ev = new SequenceEvent
                {
                    EventType  = evType,
                    DelayMs    = dto.DelayMs,
                    Address    = ParseHex(dto.Address),
                    Value      = ParseHex(dto.Value),
                    Mask       = ParseHex(dto.Mask),
                    Expected   = ParseHex(dto.Expected),
                    TimeoutMs  = dto.TimeoutMs,
                    MacAddress = dto.MacAddress,
                    VlanValid  = dto.VlanValid,
                    VlanId     = dto.VlanId,
                    Port       = dto.Port,
                    Bucket            = dto.Bucket,
                    SlotBitmap        = dto.SlotBitmap,
                    CaptureInterface  = dto.CaptureInterface ?? "",
                    CaptureFilter     = dto.CaptureFilter    ?? "",
                    CaptureExpected   = dto.CaptureExpected,
                    SerialText        = dto.SerialText        ?? "",
                    SerialHex         = dto.SerialHex         ?? ""
                };
                items.Add(new SequenceItem(ev));
            }
        }
        return items;
    }

    // ── File I/O (그룹 컬렉션) ───────────────────────────────────────────────
    public static void SaveToFile(IEnumerable<TestCaseGroup> groups, string path)
    {
        var dtos = groups.Select(g => new GroupFileDto
        {
            GroupName = g.Name,
            TestCases = g.TestCases.Select(tc => new TcFileDto
            {
                Name  = tc.Name,
                Items = tc.Items
            }).ToList()
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(dtos, Opts));
    }

    public static List<TestCaseGroup> LoadFromFile(string path)
    {
        var dtos = JsonSerializer.Deserialize<List<GroupFileDto>>(File.ReadAllText(path), Opts) ?? new();
        return dtos.Select(dto =>
        {
            var group = new TestCaseGroup { Name = dto.GroupName };
            foreach (var tc in dto.TestCases)
                group.TestCases.Add(new TestCaseEntry { Name = tc.Name, Items = tc.Items });
            return group;
        }).ToList();
    }

    // ── CSV Import ───────────────────────────────────────────────────────────
    // TC.csv  (레지스터 시나리오): TC_ID,Test_Scenario_ID,Index,Name,Address,Value,Mask,Expected,Timeout,...
    // TC1.csv (이벤트/패킷 시나리오): TC_ID,Test_Scenario_ID,Index,Name,EventType,MAC,VlanValid,VlanID,FrameRef,Timeout,...
    // TC_Packets.csv (패킷 프레임): TC_ID,FrameRef,Layer,Protocol,Field,Value
    //
    // tcCsvPath     : TC.csv 또는 TC1.csv 경로
    // packetCsvPath : TC_Packets.csv 경로 (null 가능)
    // 반환값: TC_ID 별로 그룹화된 TestCaseGroup 리스트
    public static List<TestCaseGroup> ImportFromCsv(string tcCsvPath, string? packetCsvPath = null)
    {
        // ── 1. 패킷 프레임 파싱 ─────────────────────────────────────────────
        // key: (TC_ID, FrameRef)  value: 필드 목록
        var frameMap = new Dictionary<(int tcId, string frameRef), List<(string layer, string protocol, string field, string value)>>();
        if (packetCsvPath != null && File.Exists(packetCsvPath))
        {
            var pLines = File.ReadAllLines(packetCsvPath, System.Text.Encoding.UTF8)
                             .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            foreach (var line in pLines.Skip(1))
            {
                var c = SplitCsv(line);
                if (c.Count < 6) continue;
                if (!int.TryParse(c[0].Trim(), out int tcId)) continue;
                var frameRef = c[1].Trim();
                var key = (tcId, frameRef);
                if (!frameMap.ContainsKey(key)) frameMap[key] = new();
                frameMap[key].Add((c[2].Trim(), c[3].Trim(), c[4].Trim(), c[5].Trim()));
            }
        }

        // ── 2. TC CSV 파싱 ──────────────────────────────────────────────────
        var tcLines = File.ReadAllLines(tcCsvPath, System.Text.Encoding.UTF8)
                          .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (tcLines.Count < 2) return new();

        var headers = SplitCsv(tcLines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();

        int ColIdx(params string[] names)
        {
            foreach (var n in names)
            {
                var i = headers.IndexOf(n.ToLowerInvariant());
                if (i >= 0) return i;
            }
            return -1;
        }

        int iName      = ColIdx("name");
        int iAddr      = ColIdx("address");
        int iValue     = ColIdx("value");
        int iMask      = ColIdx("mask");
        int iExpected  = ColIdx("expected");
        int iTimeout   = ColIdx("timeout");
        int iEventType = ColIdx("eventtype", "event_type", "event type");
        int iMac       = ColIdx("mac");
        int iVlanValid = ColIdx("vlanvalid", "vlan_valid", "vlan valid");
        int iVlanId    = ColIdx("vlanid", "vlan_id", "vlan id");
        int iFrameRef  = ColIdx("frameref", "frame_ref", "frame ref");
        int iTcId      = ColIdx("tc_id");
        int iScenId    = ColIdx("test_scenario_id");

        // TC_ID → (ScenarioID → 행 목록) 수집
        var tcDict = new SortedDictionary<int, List<(int scenId, List<string> cols)>>();
        foreach (var line in tcLines.Skip(1))
        {
            var c = SplitCsv(line);
            if (!int.TryParse(Safe(c, iTcId), out int tcId)) continue;
            int.TryParse(Safe(c, iScenId), out int scenId);
            if (!tcDict.ContainsKey(tcId)) tcDict[tcId] = new();
            tcDict[tcId].Add((scenId, c));
        }

        // ── 3. TestCaseGroup/Entry 생성 ─────────────────────────────────────
        // TC_ID별로 하나의 TestCaseEntry 생성 → 하나의 그룹에 담음
        var group = new TestCaseGroup { Name = Path.GetFileNameWithoutExtension(tcCsvPath), IsExpanded = true };

        foreach (var (tcId, rows) in tcDict)
        {
            var items = new List<SequenceItemDto>();
            foreach (var (_, cols) in rows.OrderBy(r => r.scenId))
            {
                var name      = Safe(cols, iName);
                var timeout   = Safe(cols, iTimeout);
                var timeoutMs = ParseTimeoutMs(timeout, 1000);

                // ── 이벤트 시나리오 포맷 (EventType 컬럼 존재) ──────────────
                if (iEventType >= 0)
                {
                    var evStr    = Safe(cols, iEventType).Trim();
                    var mac      = Safe(cols, iMac).Trim();
                    var frameRef = Safe(cols, iFrameRef).Trim();

                    if (evStr.Equals("Packet", StringComparison.OrdinalIgnoreCase))
                    {
                        // 패킷 프레임을 TC_Packets.csv에서 재구성
                        var packet = BuildPacketFromFrame(frameRef, tcId, frameMap, name);
                        items.Add(new SequenceItemDto
                        {
                            Kind       = "Packet",
                            PacketName = packet.Name,
                            Blocks     = packet.Blocks.Select(b => new SequenceItemDto.BlockDto
                            {
                                Type  = b.Type.ToString(),
                                Bytes = Convert.ToBase64String(b.Bytes)
                            }).ToList()
                        });
                        continue;
                    }

                    if (!Enum.TryParse<SequenceEventType>(evStr, true, out var evType)) continue;
                    bool.TryParse(Safe(cols, iVlanValid), out bool vlanValid);
                    int.TryParse(Safe(cols, iVlanId),    out int vlanId);

                    items.Add(new SequenceItemDto
                    {
                        Kind       = "Event",
                        EventType  = evType.ToString(),
                        DelayMs    = timeoutMs,
                        MacAddress = string.IsNullOrWhiteSpace(mac) ? "00:00:00:00:00:00" : mac.ToUpperInvariant(),
                        VlanValid  = vlanValid,
                        VlanId     = vlanId,
                        Address    = "0x00000000",
                        Value      = "0x00000000",
                        Mask       = "0xFFFFFFFF",
                        Expected   = "0x00000000",
                        TimeoutMs  = timeoutMs
                    });
                }
                else
                {
                    // ── 레지스터 시나리오 포맷 (Address/Value/Mask/Expected 컬럼) ──
                    var addr = Safe(cols, iAddr).Trim();
                    var val  = Safe(cols, iValue).Trim();
                    var mask = Safe(cols, iMask).Trim();
                    var exp  = Safe(cols, iExpected).Trim();

                    // 주소가 없는 빈 행은 건너뜀
                    if (string.IsNullOrWhiteSpace(addr)) continue;

                    // 이벤트 타입 추론: Mask+Expected 있으면 RegWaitFor, Value만 있으면 RegWrite, 아니면 RegRead
                    SequenceEventType evType;
                    if (!string.IsNullOrEmpty(mask) && mask != "-" && !string.IsNullOrEmpty(exp) && exp != "-")
                        evType = SequenceEventType.RegWaitFor;
                    else if (!string.IsNullOrEmpty(val) && val != "-")
                        evType = SequenceEventType.RegWrite;
                    else
                        evType = SequenceEventType.RegRead;

                    items.Add(new SequenceItemDto
                    {
                        Kind      = "Event",
                        EventType = evType.ToString(),
                        Address   = addr,
                        Value     = (val == "-" || string.IsNullOrEmpty(val)) ? "0x00000000" : val,
                        Mask      = (mask == "-" || string.IsNullOrEmpty(mask)) ? "0xFFFFFFFF" : mask,
                        Expected  = (exp  == "-" || string.IsNullOrEmpty(exp))  ? "0x00000000" : exp,
                        TimeoutMs = timeoutMs
                    });
                }
            }

            if (items.Count > 0)
                group.TestCases.Add(new TestCaseEntry { Name = $"TC{tcId}", Items = items });
        }

        return group.TestCases.Count > 0 ? new List<TestCaseGroup> { group } : new();
    }

    private static PacketItem BuildPacketFromFrame(
        string frameRef, int tcId,
        Dictionary<(int, string), List<(string layer, string protocol, string field, string value)>> frameMap,
        string name)
    {
        var packet = new PacketItem { Name = string.IsNullOrWhiteSpace(name) ? frameRef : name };

        if (!frameMap.TryGetValue((tcId, frameRef), out var fields) || fields.Count == 0)
        {
            // 패킷 정보 없으면 빈 이더넷 프레임
            packet.Blocks.Add(new EthernetBlock());
            return packet;
        }

        EthernetBlock? eth = null;
        RawPayloadBlock? raw = null;

        foreach (var (_, protocol, field, value) in fields.OrderBy(f => f.layer))
        {
            var proto = protocol.Trim().ToUpperInvariant();
            var fld   = field.Trim().ToUpperInvariant();
            var val   = value.Trim();

            if (proto == "ETH" || proto == "ETHERNET")
            {
                eth ??= new EthernetBlock();
                if (fld.Contains("DEST") || fld.Contains("DST"))
                    eth.DstMac = val;
                else if (fld.Contains("SRC") || fld.Contains("SOURCE"))
                    eth.SrcMac = val;
                else if (fld.Contains("TYPE"))
                    eth.EtherType = (ushort)ParseHexUint(val);
            }
            else if (proto == "RAW" || proto == "PAYLOAD" || proto == "DATA")
            {
                raw ??= new RawPayloadBlock();
                // val 이 "0x..." 형식이면 hex bytes로 파싱
                var hexStr = val.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? val[2..] : val;
                hexStr = hexStr.Replace(" ", "").Replace("-", "");
                if (hexStr.Length % 2 != 0) hexStr = "0" + hexStr;
                try
                {
                    var bytes = Enumerable.Range(0, hexStr.Length / 2)
                        .Select(i => Convert.ToByte(hexStr.Substring(i * 2, 2), 16))
                        .ToArray();
                    raw.SetBytes(bytes);
                }
                catch { raw.DataHex = val; }
            }
        }

        if (eth  != null) packet.Blocks.Add(eth);
        if (raw  != null) packet.Blocks.Add(raw);
        if (packet.Blocks.Count == 0) packet.Blocks.Add(new EthernetBlock());
        return packet;
    }

    private static string Safe(List<string> cols, int idx) =>
        idx >= 0 && idx < cols.Count ? cols[idx].Trim() : string.Empty;

    private static int ParseTimeoutMs(string? value, int fallback)
    {
        var clean = (value ?? "").Trim().Replace("ms", "", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(clean, NumberStyles.Integer, null, out var v) ? v : fallback;
    }

    private static uint ParseHexUint(string? s)
    {
        var clean = (s ?? "").Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace("_", "").Trim();
        return uint.TryParse(clean, NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"') { inQ = !inQ; }
            else if (ch == ',' && !inQ) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static uint ParseHex(string s)
    {
        var clean = s.Replace("0x", "").Replace("0X", "").Replace("_", "").Trim();
        return uint.TryParse(clean, NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    private class GroupFileDto
    {
        [JsonPropertyName("groupName")] public string GroupName { get; set; } = "";
        [JsonPropertyName("testCases")] public List<TcFileDto> TestCases { get; set; } = new();
    }

    private class TcFileDto
    {
        [JsonPropertyName("name")]  public string Name  { get; set; } = "";
        [JsonPropertyName("items")] public List<SequenceItemDto> Items { get; set; } = new();
    }
}
