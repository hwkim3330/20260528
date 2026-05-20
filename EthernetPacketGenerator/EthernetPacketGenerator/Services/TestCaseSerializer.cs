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
                    Kind           = "Event",
                    EventType      = ev.EventType.ToString(),
                    EventLabel     = string.IsNullOrWhiteSpace(ev.Label) ? null : ev.Label,
                    DelayMs        = ev.DelayMs,
                    Address        = $"0x{ev.Address:X8}",
                    Value          = $"0x{ev.Value:X8}",
                    Mask           = $"0x{ev.Mask:X8}",
                    Expected       = $"0x{ev.Expected:X8}",
                    TimeoutMs      = ev.TimeoutMs,
                    MacAddress     = ev.MacAddress,
                    VlanValid      = ev.VlanValid,
                    VlanId         = ev.VlanId,
                    Port           = ev.Port,
                    Bucket         = ev.Bucket,
                    SlotBitmap     = ev.SlotBitmap,
                    FdbExpectedMac = ev.FdbExpectedMac,
                    ExpectedDstMac = ev.ExpectedDstMac,
                    ExpectedPort   = ev.ExpectedPort
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
                // backwards compat: "RegWaitFor" / "Verify" in old .tcs files → RegVerify
                var evTypeStr = (dto.EventType ?? "")
                    .Replace("RegWaitFor", "RegVerify")
                    .Replace("\"Verify\"", "RegVerify");
                if (evTypeStr == "Verify") evTypeStr = "RegVerify";
                if (!Enum.TryParse<SequenceEventType>(evTypeStr, out var evType)) continue;
                var ev = new SequenceEvent
                {
                    EventType      = evType,
                    Label          = dto.EventLabel ?? string.Empty,
                    DelayMs        = dto.DelayMs,
                    Address        = ParseHex(dto.Address),
                    Value          = ParseHex(dto.Value),
                    Mask           = ParseHex(dto.Mask),
                    Expected       = ParseHex(dto.Expected),
                    TimeoutMs      = dto.TimeoutMs,
                    MacAddress     = dto.MacAddress ?? string.Empty,
                    VlanValid      = dto.VlanValid,
                    VlanId         = dto.VlanId,
                    Port           = evType == SequenceEventType.RxVerify ? 0 : dto.Port,
                    Bucket         = dto.Bucket,
                    SlotBitmap     = dto.SlotBitmap,
                    FdbExpectedMac = dto.FdbExpectedMac ?? string.Empty,
                    ExpectedDstMac = dto.ExpectedDstMac,
                    ExpectedPort   = dto.ExpectedPort
                };
                items.Add(new SequenceItem(ev));
            }
        }
        return items;
    }

    // ── File I/O (그룹 컬렉션) ───────────────────────────────────────────────
    private static string ScenarioDir
    {
        get
        {
            var exeDir = AppContext.BaseDirectory;
            var projectDir = Path.GetFullPath(Path.Combine(exeDir, @"..\..\..\"));
            var sourceDir = Path.Combine(projectDir, "TestScenarios");
            if (Directory.Exists(sourceDir)) return sourceDir;
            return Path.Combine(exeDir, "TestScenarios");
        }
    }

    public static void SaveToFile(IEnumerable<TestCaseGroup> groups, string path)
    {
        var scenarioDir = ScenarioDir;
        var dtos = groups.Select(g =>
        {
            // CsvSourcePath에서 TestScenarios/ 기준 상대 경로(서브폴더/파일명)만 저장
            string? csvRelPath = null;
            if (g.CsvSourcePath != null)
            {
                csvRelPath = Path.GetRelativePath(scenarioDir, g.CsvSourcePath);
                // 같은 폴더에 없으면(다른 드라이브 등) 파일명만
                if (csvRelPath.StartsWith("..")) csvRelPath = Path.GetFileName(g.CsvSourcePath);
            }

            // LastModified: CsvSourcePath 대신 그룹의 CsvLastModifiedUtc 사용 (실제 파일 없어도 저장 가능)
            string? lastModified = null;
            if (g.CsvLastModifiedUtc != null)
                lastModified = g.CsvLastModifiedUtc.Value.ToString("o");
            else if (g.CsvSourcePath != null && File.Exists(g.CsvSourcePath))
                lastModified = File.GetLastWriteTimeUtc(g.CsvSourcePath).ToString("o");

            return new GroupFileDto
            {
                GroupName       = g.Name,
                CsvFileName     = csvRelPath,
                CsvLastModified = lastModified,
                TestCases = g.TestCases.Select(tc => new TcFileDto
                {
                    Name           = tc.Name,
                    TestScenarioId = tc.TestScenarioId,
                    TcId           = tc.TcId,
                    Items          = tc.Items
                }).ToList()
            };
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(dtos, Opts));
    }

    public static List<TestCaseGroup> LoadFromFile(string path)
    {
        var scenarioDir = ScenarioDir;
        var dtos = JsonSerializer.Deserialize<List<GroupFileDto>>(File.ReadAllText(path), Opts) ?? new();
        return dtos.Select(dto =>
        {
            // 상대경로(서브폴더/파일명) → 현재 PC의 TestScenarios/ 기준 절대경로로 복원
            string? csvPath = null;
            if (!string.IsNullOrEmpty(dto.CsvFileName))
            {
                csvPath = Path.Combine(scenarioDir, dto.CsvFileName);
            }
            // 이전 버전 호환: 절대경로가 저장된 경우 파일명만 추출 후 재조합
            else if (!string.IsNullOrEmpty(dto.CsvSourcePath))
            {
                var fn = Path.GetFileName(dto.CsvSourcePath);
                csvPath = Path.Combine(scenarioDir, fn);
            }

            var group = new TestCaseGroup
            {
                Name          = dto.GroupName,
                CsvSourcePath = csvPath
            };
            if (dto.CsvLastModified != null &&
                DateTime.TryParse(dto.CsvLastModified, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                group.CsvLastModifiedUtc = dt;
            foreach (var tc in dto.TestCases)
                group.TestCases.Add(new TestCaseEntry
                {
                    Name           = tc.Name,
                    TestScenarioId = tc.TestScenarioId,
                    TcId           = tc.TcId,
                    Items          = tc.Items
                });
            return group;
        }).ToList();
    }

    // ── CSV Import ───────────────────────────────────────────────────────────
    // TC.csv  (레지스터): TC_ID,Test_Scenario_ID,Index,Name,Address,Value,Mask,Expected,Timeout,...
    // TC1.csv (이벤트):  TC_ID,Test_Scenario_ID,Index,Name,Action/EventType,Value,Expected,Timeout,...
    // TC_Fowarding_Static.csv: Test_Scenario_ID,TC_ID,Index,Name,EventType,MAC,Port,...,FrameRef,...
    // TC_Packets.csv:    Test_Scenario_ID/TC_ID,FrameRef,Layer,Protocol,Field,Value
    //
    // 반환: CSV 파일명을 이름으로 하는 TestCaseEntry 1개.
    public static TestCaseEntry? ImportCsvAsEntry(string tcCsvPath, string? packetCsvPath = null)
    {
        // ── 1. 패킷 프레임 맵 구성 (키: FrameRef 문자열만 — TC_ID 불일치 무관)
        var frameMap = new Dictionary<string,
            List<(string layer, string protocol, string field, string value)>>(
            StringComparer.OrdinalIgnoreCase);

        if (packetCsvPath != null && File.Exists(packetCsvPath))
        {
            var pktLines = File.ReadAllLines(packetCsvPath, System.Text.Encoding.UTF8);
            // 헤더에서 FrameRef 컬럼 인덱스 찾기
            // TC_Packets.csv 헤더: TC_ID,FrameRef,Layer,Protocol,Field,Value → frColIdx=1
            int frColIdx = 1; // 기본값
            if (pktLines.Length > 0)
            {
                var ph = SplitCsv(pktLines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
                // 정확한 이름 우선 검색
                var exact = ph.FindIndex(h => h == "frameref" || h == "frame_ref" || h == "frame ref");
                if (exact >= 0)
                    frColIdx = exact;
                else
                {
                    var fi = ph.FindIndex(h => h.Contains("frame") || h.Contains("ref"));
                    if (fi >= 0) frColIdx = fi;
                }
            }
            foreach (var line in pktLines.Skip(1))
            {
                var c = SplitCsv(line);
                if (c.Count < 6) continue;
                var fr = c[frColIdx].Trim();
                if (string.IsNullOrEmpty(fr) || fr == "-") continue;
                if (!frameMap.ContainsKey(fr)) frameMap[fr] = new();
                // Layer=c[frColIdx+1], Protocol=c[frColIdx+2], Field=c[frColIdx+3], Value=c[frColIdx+4]
                int off = frColIdx + 1;
                if (off + 3 < c.Count)
                    frameMap[fr].Add((c[off].Trim(), c[off+1].Trim(), c[off+2].Trim(), c[off+3].Trim()));
            }
        }

        // ── 2. TC CSV 파싱 ──────────────────────────────────────────────────
        var tcLines = File.ReadAllLines(tcCsvPath, System.Text.Encoding.UTF8)
                          .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (tcLines.Count < 2) return null;

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
        int iEventType = ColIdx("eventtype", "event_type", "event type", "action");
        int iMac       = ColIdx("mac");
        int iPort      = ColIdx("port");
        int iVlanValid = ColIdx("vlanvalid", "vlan_valid", "vlan valid");
        int iVlanId    = ColIdx("vlanid", "vlan_id", "vlan id");
        int iBucket    = ColIdx("bucket");
        int iSlotBitmap = ColIdx("slotbitmap", "slot_bitmap", "slot bitmap",
                                 "slot_bitmask", "slotbitmask", "slot_bit_map",
                                 "slot_map", "slotmap", "slot");
        int iFrameRef  = ColIdx("frameref", "frame_ref", "frame ref");
        int iTcId      = ColIdx("tc_id");
        int iScenId    = ColIdx("test_scenario_id");
        int iIndex     = ColIdx("index");

        // ── 3. 전체 행을 (TC_ID, ScenID, Index) 순서로 정렬해 순서대로 처리 ─
        var rows = tcLines.Skip(1)
            .Select(l => SplitCsv(l))
            .Where(c => int.TryParse(Safe(c, iTcId), out _))
            .Select(c =>
            {
                int.TryParse(Safe(c, iTcId),   out int tid);
                int.TryParse(Safe(c, iScenId), out int sid);
                int.TryParse(Safe(c, iIndex),  out int idx);
                return (tid, sid, idx, cols: c);
            })
            .OrderBy(r => r.tid).ThenBy(r => r.sid).ThenBy(r => r.idx)
            .ToList();

        var items = new List<SequenceItemDto>();

        foreach (var (tcId, _, _, cols) in rows)
        {
            var name      = Safe(cols, iName).Trim();
            var timeoutMs = ParseTimeoutMs(Safe(cols, iTimeout), 1000);

            // ── 이벤트 타입 결정 ─────────────────────────────────────────
            // Action/EventType 컬럼이 있으면 그 값 사용
            // 없으면 Address/Value/Mask/Expected 컬럼 패턴으로 추론
            string evStr;
            if (iEventType >= 0)
            {
                evStr = Safe(cols, iEventType).Trim();
            }
            else
            {
                // 레거시 포맷 (TC.csv): Address+Value+Mask+Expected 패턴으로 추론
                var addrL = Safe(cols, iAddr).Trim();
                var valL  = Safe(cols, iValue).Trim();
                var maskL = Safe(cols, iMask).Trim();
                var expL  = Safe(cols, iExpected).Trim();
                bool hasAddr = !string.IsNullOrEmpty(addrL) && addrL != "-";
                bool hasVal  = !string.IsNullOrEmpty(valL)  && valL  != "-";
                bool hasMask = !string.IsNullOrEmpty(maskL) && maskL != "-";
                bool hasExp  = !string.IsNullOrEmpty(expL)  && expL  != "-";

                if (!hasAddr) continue;   // 주소 없으면 빈 행 skip

                if (hasMask && hasExp)
                    evStr = "RegVerify";
                else if (hasVal)
                    evStr = "RegWrite";
                else
                    evStr = "RegRead";
            }
                var mac      = Safe(cols, iMac).Trim();
                var frameRef = Safe(cols, iFrameRef).Trim();
                var portRaw  = Safe(cols, iPort).Trim();

                if (string.IsNullOrEmpty(evStr)) continue;

                // backwards compat: RegWaitFor → Verify
                if (evStr.Equals("RegWaitFor", StringComparison.OrdinalIgnoreCase))
                    evStr = "RegVerify";

                // FdbInitialize → FdbFlush として扱う
                if (evStr.Equals("FdbInitialize", StringComparison.OrdinalIgnoreCase))
                    evStr = "FdbFlush";

                if (evStr.Equals("Packet", StringComparison.OrdinalIgnoreCase))
                {
                    PacketItem packet;
                    var valueMac = Safe(cols, iValue).Trim();  // TC1.csv: Value 컬럼에 DST MAC
                    if (!string.IsNullOrEmpty(frameRef) && frameRef != "-" &&
                        frameMap.ContainsKey(frameRef))
                    {
                        // FrameRef 기반 패킷 (TC_Fowarding_Static.csv 포맷)
                        packet = BuildPacketFromFrame(frameRef, frameMap, name);
                    }
                    else if (!string.IsNullOrEmpty(valueMac) && valueMac != "-" &&
                             valueMac.Contains(':'))
                    {
                        // Value 컬럼이 MAC 주소인 경우 직접 생성 (TC1.csv 포맷)
                        packet = BuildSimplePacket(name, valueMac.ToUpperInvariant());
                    }
                    else
                    {
                        packet = BuildPacketFromFrame(frameRef, frameMap, name);
                    }
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

                // RxVerify: DA 기반 수신 검증 이벤트
                // Expected 컬럼 = 수신 기대 MAC (ExpectedDstMac)
                // Port 컬럼 = 수신 기대 포트 인덱스 (ExpectedPort), 없으면 -1
                if (evStr.Equals("RxVerify", StringComparison.OrdinalIgnoreCase))
                {
                    var rxExpectedMac  = Safe(cols, iExpected).Trim();
                    var rxExpectedPort = -1;
                    if (!string.IsNullOrEmpty(portRaw) && portRaw != "-")
                        int.TryParse(portRaw, out rxExpectedPort);
                    items.Add(new SequenceItemDto
                    {
                        Kind           = "Event",
                        EventType      = SequenceEventType.RxVerify.ToString(),
                        TimeoutMs      = timeoutMs,
                        ExpectedDstMac = (string.IsNullOrWhiteSpace(rxExpectedMac) || rxExpectedMac == "-")
                                          ? string.Empty : rxExpectedMac.ToUpperInvariant(),
                        ExpectedPort   = rxExpectedPort
                    });
                    continue;
                }

                if (!Enum.TryParse<SequenceEventType>(evStr, true, out var evType)) continue;
                bool.TryParse(Safe(cols, iVlanValid), out bool vlanValid);
                int.TryParse(Safe(cols, iVlanId), out int vlanId);

                // MAC: MAC 컬럼 우선, 없으면 Value 컬럼 (TC1.csv 포맷)
                var macRaw = mac;
                if (string.IsNullOrWhiteSpace(macRaw) || macRaw == "-")
                    macRaw = Safe(cols, iValue).Trim();

                var addrStr = Safe(cols, iAddr).Trim();
                var valStr  = Safe(cols, iValue).Trim();
                var maskStr = Safe(cols, iMask).Trim();
                var expStr  = Safe(cols, iExpected).Trim();

                // Port: Port 컬럼 우선, 없으면 Expected 컬럼 (TC1.csv 포맷)
                // FdbReadBucket은 Expected에 MAC이 올 수 있으므로 fallback 금지
                var portSrc = (!string.IsNullOrEmpty(portRaw) && portRaw != "-")
                    ? portRaw
                    : (evType == SequenceEventType.FdbReadBucket ? "" : expStr);
                int portVal = 0;
                if (!string.IsNullOrEmpty(portSrc) && portSrc != "-")
                {
                    if (portSrc.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                        // 비트마스크 표기 → 비트마스크 값 그대로 저장 (0b000001=1, 0b100000=32)
                        portVal = Convert.ToInt32(portSrc[2..], 2);
                    else
                        portVal = (int)ParseHexUint(portSrc);
                }

                // FdbReadBucket: Expected 컬럼 → FdbExpectedMac (비어있으면 순수 읽기)
                string fdbExpectedMac = string.Empty;
                if (evType == SequenceEventType.FdbReadBucket &&
                    !string.IsNullOrWhiteSpace(expStr) && expStr != "-")
                    fdbExpectedMac = expStr.Trim().Replace("-", ":").ToUpperInvariant();

                // Bucket / SlotBitmap 컬럼
                int.TryParse(Safe(cols, iBucket), out int bucketVal);
                int slotBitmapVal = 1;
                var slotRaw = Safe(cols, iSlotBitmap).Trim();
                if (!string.IsNullOrEmpty(slotRaw) && slotRaw != "-")
                    slotBitmapVal = (int)ParseHexUint(slotRaw);

                // MAC 주소: 정규화, 없으면 기본값
                var macAddr = (string.IsNullOrWhiteSpace(macRaw) || macRaw == "-")
                    ? "00:00:00:00:00:00"
                    : macRaw.Trim().Replace("-", ":").ToUpperInvariant();

                // EventLabel: Name 컬럼 (비어 있으면 저장하지 않음)
                string? eventLabel = string.IsNullOrWhiteSpace(name) ? null : name;

                items.Add(new SequenceItemDto
                {
                    Kind           = "Event",
                    EventType      = evType.ToString(),
                    EventLabel     = eventLabel,
                    DelayMs        = timeoutMs,
                    MacAddress     = macAddr,
                    Port           = portVal,
                    VlanValid      = vlanValid,
                    VlanId         = vlanId,
                    Bucket         = bucketVal,
                    SlotBitmap     = slotBitmapVal,
                    FdbExpectedMac = fdbExpectedMac,
                    Address        = (string.IsNullOrEmpty(addrStr) || addrStr == "-") ? "0x00000000" : addrStr,
                    Value          = (string.IsNullOrEmpty(valStr)  || valStr  == "-") ? "0x00000000" : valStr,
                    Mask           = (string.IsNullOrEmpty(maskStr) || maskStr == "-") ? "0xFFFFFFFF" : maskStr,
                    Expected       = (string.IsNullOrEmpty(expStr)  || expStr  == "-") ? "0x00000000" : expStr,
                    TimeoutMs      = timeoutMs
                });
        }

        if (items.Count == 0) return null;

        var firstRow = rows.FirstOrDefault();
        return new TestCaseEntry
        {
            Name           = Path.GetFileNameWithoutExtension(tcCsvPath),
            TestScenarioId = firstRow.sid,
            TcId           = firstRow.tid,
            Items          = items
        };
    }

    // 하위 호환: 이전 코드에서 List<TestCaseGroup>을 기대하는 호출부를 위한 래퍼
    public static List<TestCaseGroup> ImportFromCsv(string tcCsvPath, string? packetCsvPath = null)
    {
        var entry = ImportCsvAsEntry(tcCsvPath, packetCsvPath);
        if (entry == null) return new();
        var group = new TestCaseGroup
        {
            Name       = entry.Name,
            IsExpanded = true
        };
        group.TestCases.Add(entry);
        return new List<TestCaseGroup> { group };
    }

    private static PacketItem BuildSimplePacket(string name, string dstMac)
    {
        var packet = new PacketItem { Name = string.IsNullOrWhiteSpace(name) ? dstMac : name };
        packet.Blocks.Add(new EthernetBlock
        {
            DstMac    = dstMac,
            SrcMac    = "9C:6B:00:49:3A:32",
            EtherType = 0x88B5
        });
        var raw = new RawPayloadBlock();
        raw.ImportBytes(System.Text.Encoding.ASCII.GetBytes("KETI-FDB-FORWARDING-TEST"), 0);
        packet.Blocks.Add(raw);
        return packet;
    }

    private static PacketItem BuildPacketFromFrame(
        string frameRef,
        Dictionary<string, List<(string layer, string protocol, string field, string value)>> frameMap,
        string name)
    {
        var packet = new PacketItem { Name = string.IsNullOrWhiteSpace(name) ? frameRef : name };

        if (!frameMap.TryGetValue(frameRef, out var fields) || fields.Count == 0)
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
                    eth.EtherType = ParseEtherType(val);
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

    private static ushort ParseEtherType(string? val)
    {
        var s = (val ?? "").Trim();
        // 이름으로 된 EtherType 처리 (ARP, IPv4, IPv6, ...)
        return s.ToUpperInvariant() switch
        {
            "ARP"  => 0x0806,
            "IPV4" or "IP" => 0x0800,
            "IPV6" => 0x86DD,
            "VLAN" => 0x8100,
            "LLDP" => 0x88CC,
            _ => (ushort)ParseHexUint(s)
        };
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
        [JsonPropertyName("groupName")]       public string  GroupName       { get; set; } = "";
        [JsonPropertyName("csvFileName")]     public string? CsvFileName     { get; set; }
        [JsonPropertyName("csvSourcePath")]   public string? CsvSourcePath   { get; set; } // 이전 버전 호환용
        [JsonPropertyName("csvLastModified")] public string? CsvLastModified { get; set; }
        [JsonPropertyName("testCases")]       public List<TcFileDto> TestCases { get; set; } = new();
    }

    private class TcFileDto
    {
        [JsonPropertyName("name")]             public string Name           { get; set; } = "";
        [JsonPropertyName("testScenarioId")]   public int    TestScenarioId { get; set; }
        [JsonPropertyName("tcId")]             public int    TcId           { get; set; }
        [JsonPropertyName("items")]            public List<SequenceItemDto> Items { get; set; } = new();
    }
}
