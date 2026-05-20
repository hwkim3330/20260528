using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using EthernetPacketGenerator.ViewModels;
using SharpPcap;

namespace EthernetPacketGenerator.Services;

/// <summary>
/// TcpListener 기반 경량 HTTP 서버 (관리자 권한 불필요).
/// GET  /api/interfaces  — NIC 목록 + 선택 인터페이스
/// POST /api/build       — 프레임 빌드 (frameHex + decoded)
/// POST /api/send        — 프레임 전송
/// 기본 포트: 8080
/// </summary>
public sealed class LabApiServer : IDisposable
{
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public int Port { get; }
    public bool IsRunning { get; private set; }

    /// <summary>SendViewModel이 갱신 — /api/interfaces 응답에 포함 (Default 인터페이스 이름)</summary>
    public string? SelectedInterfaceName { get; set; }

    /// <summary>SendViewModel이 갱신 — /api/send 시 실제 전송에 사용 (Default 디바이스)</summary>
    public ILiveDevice? ActiveDevice { get; set; }

    /// <summary>SendViewModel이 갱신 — /api/interfaces 응답의 activeInterfaces 목록에 사용</summary>
    public List<EthernetPacketGenerator.Models.InterfaceEntry> ActiveInterfaceEntries { get; set; } = new();

    /// <summary>App.xaml.cs 에서 주입 — /api/auto/* 엔드포인트에서 사용</summary>
    public AutomationViewModel? AutomationVm { get; set; }
    public CaptureViewModel? CaptureVm { get; set; }
    public HyperTerminalViewModel? HyperTerminalVm { get; set; }
    public MainViewModel? MainVm { get; set; }

    // ── Packet Flow Monitor 상태 ──────────────────────────────────────────────
    private readonly PacketFlowMonitorService _pfm = new();
    private volatile bool    _pfmRunning;
    private string           _pfmSessionId = string.Empty;
    private FlowInterfaceState? _pfmTxSt;
    private FlowInterfaceState? _pfmP1St;
    private FlowInterfaceState? _pfmP2St;
    private FlowInterfaceState? _pfmP3St;
    private string           _pfmFinalResult = string.Empty;
    private string           _pfmFinalReason = string.Empty;
    private string           _pfmMode        = string.Empty;
    private int              _pfmExpPort;
    private bool             _pfmChkTx  = true;
    private bool             _pfmChkUnx = true;
    private bool             _pfmExP1 = true, _pfmExP2 = true, _pfmExP3 = true;

    // ── Log persistence (3100 기능 통합) ─────────────────────────────────────
    private readonly Dictionary<string, JsonObject> _runningTests  = new();
    private readonly Dictionary<string, JsonObject> _runningMacros = new();
    private readonly string _testsDir;
    private readonly string _macrosDir;

    public LabApiServer(int port = 8080)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);

        // 로그 폴더: exe 옆 logs/tests, logs/macros
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _testsDir  = Path.Combine(baseDir, "logs", "tests");
        _macrosDir = Path.Combine(baseDir, "logs", "macros");
        Directory.CreateDirectory(_testsDir);
        Directory.CreateDirectory(_macrosDir);
    }

    public void Start()
    {
        _listener.Start();
        IsRunning = true;
        _cts = new CancellationTokenSource();
        Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
    }

    // ── Accept loop ───────────────────────────────────────────────────────────

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client), token);
        }
    }

    // ── Request handler ───────────────────────────────────────────────────────

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();

                // 헤더 + 바디 읽기. TCP 분절로 헤더와 바디가 따로 올 수 있으므로
                // Content-Length만큼 모두 수신한 뒤 파싱한다.
                var buf   = new byte[65536];
                int n     = await stream.ReadAsync(buf).ConfigureAwait(false);
                int bodyDelim = -1;

                // 헤더 끝(\r\n\r\n) 탐색 — 미발견 시 추가 읽기
                while (n < buf.Length)
                {
                    var headerScan = Encoding.ASCII.GetString(buf, 0, n);
                    bodyDelim = headerScan.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (bodyDelim >= 0)
                    {
                        // Content-Length 파싱 후 바디 완전 수신
                        var headers = headerScan[..bodyDelim];
                        var clm = System.Text.RegularExpressions.Regex.Match(
                            headers, @"Content-Length:\s*(\d+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (clm.Success)
                        {
                            int contentLen = int.Parse(clm.Groups[1].Value);
                            int bodyRecv   = n - (bodyDelim + 4);
                            while (bodyRecv < contentLen && n < buf.Length)
                            {
                                int r = await stream.ReadAsync(buf, n, buf.Length - n).ConfigureAwait(false);
                                if (r == 0) break;
                                n       += r;
                                bodyRecv += r;
                            }
                        }
                        break;
                    }
                    // 헤더 끝 아직 미도착 — 추가 읽기
                    int more = await stream.ReadAsync(buf, n, buf.Length - n).ConfigureAwait(false);
                    if (more == 0) break;
                    n += more;
                }

                if (bodyDelim < 0)
                {
                    var scan = Encoding.ASCII.GetString(buf, 0, n);
                    bodyDelim = scan.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                }

                var raw  = Encoding.UTF8.GetString(buf, 0, n);
                var requestLine = raw.Split('\n')[0].Trim();
                var body        = bodyDelim >= 0 ? raw[(bodyDelim + 4)..].TrimEnd('\0') : string.Empty;

                string responseBody;
                int    status;

                // ── Static file serving (non-API GET) ────────────────────────────
                var urlPath = requestLine.Split(' ').Length > 1 ? requestLine.Split(' ')[1] : "/";
                bool isApiRequest = urlPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

                if (!isApiRequest && requestLine.StartsWith("GET /", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeStaticAsync(stream, urlPath).ConfigureAwait(false);
                    return;
                }

                // CORS preflight
                if (requestLine.StartsWith("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    var pre = "HTTP/1.1 204 No Content\r\n" +
                              "Access-Control-Allow-Origin: *\r\n" +
                              "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                              "Access-Control-Allow-Headers: Content-Type\r\n" +
                              "Content-Length: 0\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(pre)).ConfigureAwait(false);
                    return;
                }

                if (requestLine.StartsWith("GET /api/interfaces", StringComparison.OrdinalIgnoreCase))
                {
                    // activeInterfaces: IsActive 체크된 항목의 OS 인터페이스 이름 (MAC 매칭)
                    var activeNames = new JsonArray();
                    foreach (var e in ActiveInterfaceEntries.Where(e => e.IsActive))
                    {
                        var osName = GetOsInterfaceName(e.Device) ?? e.ShortName;
                        activeNames.Add(JsonValue.Create(osName));
                    }

                    // selectedInterface: Default 디바이스의 OS 이름 (probe 드롭다운 자동선택용)
                    var defaultOsName = GetOsInterfaceName(ActiveDevice) ?? SelectedInterfaceName;

                    var payload = new JsonObject
                    {
                        ["ok"]                = true,
                        ["interfaces"]        = BuildInterfaceList(),
                        ["selectedInterface"] = defaultOsName,
                        ["defaultInterface"]  = defaultOsName,
                        ["activeInterfaces"]  = activeNames
                    };
                    responseBody = payload.ToJsonString();
                    status       = 200;
                }
                else if (requestLine.StartsWith("POST /api/build", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleBuild(body);
                }
                else if (requestLine.StartsWith("POST /api/send", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = await HandleSendAsync(body).ConfigureAwait(false);
                }
                else if (requestLine.StartsWith("POST /api/packet-flow/start", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = await HandlePfmStartAsync(body).ConfigureAwait(false);
                }
                else if (requestLine.StartsWith("GET /api/packet-flow/status", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandlePfmStatus();
                }
                else if (requestLine.StartsWith("POST /api/packet-flow/stop", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandlePfmStop();
                }
                else if (requestLine.StartsWith("GET /api/health", StringComparison.OrdinalIgnoreCase))
                {
                    var h = new JsonObject
                    {
                        ["ok"]      = true,
                        ["service"] = "LabApiServer",
                        ["port"]    = Port,
                        ["time"]    = DateTime.UtcNow.ToString("o")
                    };
                    responseBody = h.ToJsonString();
                    status       = 200;
                }
                // ── Log tracking (3100 통합) ───────────────────────────────────
                else if (requestLine.StartsWith("POST /api/log/macro/", StringComparison.OrdinalIgnoreCase)
                         && requestLine.Contains("/step", StringComparison.OrdinalIgnoreCase))
                {
                    // POST /api/log/macro/:macroId/step  (더 구체적인 것 먼저)
                    var parts   = requestLine.Split(' ')[1].Split('/');
                    var macroId = parts.Length >= 5 ? parts[4] : string.Empty;
                    (responseBody, status) = HandleLogMacroStep(macroId, body);
                }
                else if (requestLine.StartsWith("POST /api/log/macro/start", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleLogMacroStart(body);
                }
                else if (requestLine.StartsWith("POST /api/log/start", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleLogStart(body);
                }
                else if (requestLine.StartsWith("POST /api/log/result", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleLogResult(body);
                }
                else if (requestLine.StartsWith("GET /api/logs", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleLogList();
                }
                else if (requestLine.StartsWith("GET /api/log/", StringComparison.OrdinalIgnoreCase))
                {
                    var testId = requestLine.Split(' ')[1].Split('/').LastOrDefault() ?? string.Empty;
                    (responseBody, status) = HandleLogGet(testId);
                }
                // ── Automation API ─────────────────────────────────────────────────
                else if (requestLine.StartsWith("POST /api/auto/run", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = await HandleAutoRunAsync(body).ConfigureAwait(false);
                }
                else if (requestLine.StartsWith("GET /api/auto/status", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleAutoStatus();
                }
                else if (requestLine.StartsWith("GET /api/auto/results", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleAutoResults();
                }
                else if (requestLine.StartsWith("GET /api/capture/status", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleCaptureStatus();
                }
                else if (requestLine.StartsWith("POST /api/capture/start", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleCaptureStart(body);
                }
                else if (requestLine.StartsWith("POST /api/capture/stop", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleCaptureStop();
                }
                else if (requestLine.StartsWith("POST /api/capture/clear", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleCaptureClear();
                }
                else if (requestLine.StartsWith("GET /api/capture/packets", StringComparison.OrdinalIgnoreCase))
                {
                    var path = requestLine.Split(' ')[1];
                    (responseBody, status) = HandleCapturePackets(path);
                }
                else if (requestLine.StartsWith("GET /api/serial/status", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleSerialStatus();
                }
                else if (requestLine.StartsWith("POST /api/serial/connect", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleSerialConnect(body);
                }
                else if (requestLine.StartsWith("POST /api/serial/disconnect", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleSerialDisconnect();
                }
                else if (requestLine.StartsWith("POST /api/serial/send", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleSerialSend(body);
                }
                else if (requestLine.StartsWith("POST /api/serial/clear", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleSerialClear();
                }
                else if (requestLine.StartsWith("GET /api/register/status", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleRegisterStatus();
                }
                else if (requestLine.StartsWith("POST /api/register/read", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = await HandleRegisterReadAsync(body).ConfigureAwait(false);
                }
                else if (requestLine.StartsWith("POST /api/register/write", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = await HandleRegisterWriteAsync(body).ConfigureAwait(false);
                }
                else if (requestLine.StartsWith("POST /api/fdb/read", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = await HandleFdbReadAsync(body).ConfigureAwait(false);
                }
                else if (requestLine.StartsWith("POST /api/fdb/write", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = await HandleFdbWriteAsync(body).ConfigureAwait(false);
                }
                else if (requestLine.StartsWith("POST /api/fdb/delete", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = await HandleFdbDeleteAsync(body).ConfigureAwait(false);
                }
                else if (requestLine.StartsWith("POST /api/fdb/flush", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = await HandleFdbFlushAsync().ConfigureAwait(false);
                }
                else if (requestLine.StartsWith("GET /api/app/status", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleAppStatus();
                }
                else if (requestLine.StartsWith("GET /api/testcases/status", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleTestCasesStatus();
                }
                else if (requestLine.StartsWith("POST /api/testcases/add-group", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleTestCasesAddGroup(body);
                }
                else if (requestLine.StartsWith("POST /api/testcases/add", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleTestCasesAdd(body);
                }
                else if (requestLine.StartsWith("POST /api/testcases/select", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleTestCasesSelect(body);
                }
                else if (requestLine.StartsWith("POST /api/testcases/save-current", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleTestCasesSaveCurrent();
                }
                else if (requestLine.StartsWith("POST /api/testcases/delete", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleTestCasesDelete(body);
                }
                else if (requestLine.StartsWith("GET /api/sequence/status", StringComparison.OrdinalIgnoreCase))
                {
                    (responseBody, status) = HandleSequenceStatus();
                }
                else
                {
                    responseBody = "{\"ok\":false,\"error\":\"not found\"}";
                    status       = 404;
                }

                var bodyBytes   = Encoding.UTF8.GetBytes(responseBody);
                var statusText  = status == 200 ? "OK" : status == 400 ? "Bad Request" : "Not Found";
                var headerBytes = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} {statusText}\r\n" +
                    $"Content-Type: application/json; charset=utf-8\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    $"Access-Control-Allow-Origin: *\r\n" +
                    $"Connection: close\r\n\r\n");

                await stream.WriteAsync(headerBytes).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
            }
            catch { /* 연결 끊김 등 무시 */ }
        }
    }

    // ── POST /api/build ───────────────────────────────────────────────────────

    private static (string body, int status) HandleBuild(string jsonBody)
    {
        try
        {
            var profile = JsonNode.Parse(jsonBody) as JsonObject
                          ?? throw new ArgumentException("invalid JSON");
            var (frame, decoded) = LabPacketService.BuildFrame(profile);
            var result = new JsonObject
            {
                ["ok"]     = true,
                ["stdout"] = new JsonObject
                {
                    ["frameHex"] = Convert.ToHexString(frame).ToLower(),
                    ["decoded"]  = decoded
                }
            };
            return (result.ToJsonString(), 200);
        }
        catch (Exception ex)
        {
            return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400);
        }
    }

    // ── POST /api/send ────────────────────────────────────────────────────────

    private async Task<(string body, int status)> HandleSendAsync(string jsonBody)
    {
        try
        {
            var profile = JsonNode.Parse(jsonBody) as JsonObject
                          ?? throw new ArgumentException("invalid JSON");

            // profile["interface"]에 OS 인터페이스 이름이 있으면 해당 디바이스를 우선 사용,
            // 없거나 매칭 실패 시 ActiveDevice(Default) 사용
            var ifaceName = profile["interface"]?.GetValue<string>();
            var dev = (ifaceName != null ? ResolveDevice(ifaceName) : null)
                      ?? ActiveDevice
                      ?? throw new InvalidOperationException("No interface selected in the app");

            var count      = profile["count"]?.GetValue<int>()    ?? 1;
            var intervalMs = profile["intervalMs"]?.GetValue<double>() ?? 0;
            var recordTs   = profile["recordTimestamps"]?.GetValue<bool>() ?? false;
            var payloadObj = profile["payload"] as JsonObject;
            var isBench    = payloadObj?["mode"]?.GetValue<string>() == "benchmark";
            var seqStart   = payloadObj?["start"]?.GetValue<int>() ?? 1;

            var txRecords  = new JsonArray();
            JsonObject? lastDecoded = null;
            long bytesSent = 0;

            var sw    = Stopwatch.StartNew();
            var swRef = Stopwatch.GetTimestamp();

            for (int i = 0; i < count; i++)
            {
                var (frame, decoded) = LabPacketService.BuildFrame(profile, seqStart + i);
                lastDecoded = decoded;

                long txNs = LabPacketService.HighResNs();

                dev.SendPacket(frame);
                bytesSent += frame.Length;

                if (recordTs || isBench)
                {
                    txRecords.Add(new JsonObject
                    {
                        ["seq"]            = seqStart + i,
                        ["txTimestampNs"]  = txNs,
                        ["length"]         = frame.Length
                    });
                }

                if (intervalMs > 0 && i < count - 1)
                    await PreciseDelayAsync(intervalMs).ConfigureAwait(false);
            }

            sw.Stop();

            var result = new JsonObject
            {
                ["ok"]     = true,
                ["stdout"] = new JsonObject
                {
                    ["framesSent"]      = count,
                    ["bytesSent"]       = bytesSent,
                    ["elapsedSec"]      = sw.Elapsed.TotalSeconds,
                    ["decoded"]         = lastDecoded,
                    ["txRecords"]       = txRecords
                }
            };
            return (result.ToJsonString(), 200);
        }
        catch (Exception ex)
        {
            return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400);
        }
    }

    // ── Delay helper (짧은 인터벌은 SpinWait, 긴 것은 Task.Delay) ─────────────

    private static Task PreciseDelayAsync(double ms)
    {
        if (ms >= 15)
            return Task.Delay((int)ms);

        // 15ms 미만: SpinWait 기반 정밀 대기
        var target = (long)(ms * Stopwatch.Frequency / 1000.0);
        var start  = Stopwatch.GetTimestamp();
        return Task.Run(() =>
        {
            while (Stopwatch.GetTimestamp() - start < target)
                Thread.SpinWait(50);
        });
    }

    // ── SharpPcap 디바이스 → OS 인터페이스 이름 ──────────────────────────────

    /// <summary>MAC 매칭으로 SharpPcap 디바이스에 해당하는 OS NIC 이름을 반환한다.</summary>
    private static string? GetOsInterfaceName(ILiveDevice? dev)
    {
        if (dev == null) return null;
        try
        {
            var macBytes = dev.MacAddress?.GetAddressBytes();
            if (macBytes?.Length != 6) return null;
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n =>
                {
                    var b = n.GetPhysicalAddress().GetAddressBytes();
                    return b.Length == 6 && b.SequenceEqual(macBytes);
                })?.Name;
        }
        catch { return null; }
    }

    // ── 인터페이스 이름 → SharpPcap 디바이스 해석 ────────────────────────────

    /// <summary>
    /// OS 인터페이스 이름(예: "Ethernet 2")으로 MAC을 찾고,
    /// ActiveInterfaceEntries에서 동일 MAC인 SharpPcap 디바이스를 반환한다.
    /// 없거나 열리지 않은 경우 열기를 시도하고 반환; 실패 시 null.
    /// </summary>
    private ILiveDevice? ResolveDevice(string osIfaceName)
    {
        if (string.IsNullOrWhiteSpace(osIfaceName)) return null;

        // OS NIC 이름 → MAC
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Name.Equals(osIfaceName, StringComparison.OrdinalIgnoreCase));
        if (nic == null) return null;

        var macBytes = nic.GetPhysicalAddress().GetAddressBytes();
        if (macBytes.Length != 6) return null;

        // 1차: ActiveInterfaceEntries에서 MAC 매칭
        var entry = ActiveInterfaceEntries.FirstOrDefault(e =>
        {
            try
            {
                var devMac = e.Device?.MacAddress?.GetAddressBytes();
                return devMac?.Length == 6 && devMac.SequenceEqual(macBytes);
            }
            catch { return false; }
        });

        if (entry?.Device != null)
        {
            try { entry.Device.Open(DeviceModes.None); } catch { }
            return entry.Device;
        }

        // 2차 폴백: 등록되지 않은(IsActive=false 등) 인터페이스도 포함해 전체 SharpPcap 디바이스 검색
        try
        {
            foreach (var dev in SharpPcap.CaptureDeviceList.Instance)
            {
                try
                {
                    var devMac = dev.MacAddress?.GetAddressBytes();
                    if (devMac?.Length == 6 && devMac.SequenceEqual(macBytes))
                    {
                        dev.Open(DeviceModes.None);
                        return dev;
                    }
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    // ── /api/interfaces 헬퍼 ─────────────────────────────────────────────────

    private static JsonArray BuildInterfaceList()
    {
        var arr = new JsonArray();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var macBytes = nic.GetPhysicalAddress().GetAddressBytes();
            if (macBytes.Length != 6) continue;

            var mac   = string.Join(":", macBytes.Select(b => b.ToString("x2")));
            var state = nic.OperationalStatus == OperationalStatus.Up ? "up" : "down";

            int mtu = 1500;
            try { mtu = nic.GetIPProperties().GetIPv4Properties()?.Mtu ?? 1500; } catch { }

            var ipv4 = new JsonArray();
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                ipv4.Add(new JsonObject { ["local"] = ua.Address.ToString(), ["prefixlen"] = ua.PrefixLength });
            }

            arr.Add(new JsonObject
            {
                ["name"]  = nic.Name,
                ["mac"]   = mac,
                ["state"] = state,
                ["mtu"]   = mtu,
                ["ipv4"]  = ipv4
            });
        }
        return arr;
    }

    // ── POST /api/packet-flow/start ──────────────────────────────────────────

#pragma warning disable CS1998
    private async Task<(string body, int status)> HandlePfmStartAsync(string jsonBody)
    {
        if (_pfmRunning)
            return ("{\"ok\":false,\"error\":\"already running\"}", 400);

        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject
                      ?? throw new ArgumentException("invalid JSON");

            _pfmMode   = req["flowMode"]?.GetValue<string>()           ?? "TX Only";
            _pfmExpPort= req["expectedOutputPort"]?.GetValue<int>()    ?? 1;
            _pfmChkTx  = req["checkTxObserved"]?.GetValue<bool>()      ?? true;
            _pfmChkUnx = req["checkUnexpectedPorts"]?.GetValue<bool>() ?? true;
            var exPorts= req["expectedPorts"]?.AsArray();
            _pfmExP1   = exPorts?.Any(n => n?.GetValue<int>() == 1) ?? true;
            _pfmExP2   = exPorts?.Any(n => n?.GetValue<int>() == 2) ?? true;
            _pfmExP3   = exPorts?.Any(n => n?.GetValue<int>() == 3) ?? true;

            var ifaces     = req["interfaces"] as JsonObject;
            var txName     = ifaces?["tx"]?.GetValue<string>()    ?? string.Empty;
            var p1Name     = ifaces?["port1"]?.GetValue<string>() ?? string.Empty;
            var p2Name     = ifaces?["port2"]?.GetValue<string>() ?? string.Empty;
            var p3Name     = ifaces?["port3"]?.GetValue<string>() ?? string.Empty;
            int timeoutSec = req["captureTimeoutSec"]?.GetValue<int>() ?? 5;

            // Filter fields
            var dstMac    = req["dstMac"]?.GetValue<string>()    ?? string.Empty;
            var srcMac    = req["srcMac"]?.GetValue<string>()    ?? string.Empty;
            var etherType = req["etherType"]?.GetValue<string>() ?? string.Empty;
            var signature = req["signature"]?.GetValue<string>() ?? string.Empty;
            var udpSrc    = req["udpSrcPort"]?.GetValue<string>() ?? string.Empty;
            var udpDst    = req["udpDstPort"]?.GetValue<string>() ?? string.Empty;

            var config = new PacketFlowMonitorConfig
            {
                TxDevice           = string.IsNullOrEmpty(txName) ? null : ResolveDevice(txName),
                Port1Device        = string.IsNullOrEmpty(p1Name) ? null : ResolveDevice(p1Name),
                Port2Device        = string.IsNullOrEmpty(p2Name) ? null : ResolveDevice(p2Name),
                Port3Device        = string.IsNullOrEmpty(p3Name) ? null : ResolveDevice(p3Name),
                TxInterfaceName    = txName,
                Port1InterfaceName = p1Name,
                Port2InterfaceName = p2Name,
                Port3InterfaceName = p3Name,
                DstMac            = dstMac,
                SrcMac            = srcMac,
                EtherType         = etherType,
                Signature         = signature,
                UdpSrcPort        = udpSrc,
                UdpDstPort        = udpDst,
                CaptureTimeoutSec = timeoutSec,
                MaxDetailPackets  = 0
            };

            _pfmSessionId   = Guid.NewGuid().ToString("N")[..8];
            _pfmFinalResult = string.Empty;
            _pfmFinalReason = string.Empty;
            _pfmRunning     = true;
            _pfmTxSt = _pfmP1St = _pfmP2St = _pfmP3St = null;

            // Run capture in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _pfm.RunAsync(config, (_, st) =>
                    {
                        _pfmTxSt = st.Role == "TX" ? st : _pfmTxSt;
                        if (st.Role == "RX")
                        {
                            if (st.SwitchPort == 1) _pfmP1St = st;
                            else if (st.SwitchPort == 2) _pfmP2St = st;
                            else if (st.SwitchPort == 3) _pfmP3St = st;
                        }
                    }, CancellationToken.None);

                    _pfmTxSt = result.TxState;
                    _pfmP1St = result.Port1State;
                    _pfmP2St = result.Port2State;
                    _pfmP3St = result.Port3State;
                    (_pfmFinalResult, _pfmFinalReason) = JudgePfm();
                }
                finally { _pfmRunning = false; }
            });

            var resp = new JsonObject
            {
                ["ok"]       = true,
                ["sessionId"]= _pfmSessionId,
                ["status"]   = "running"
            };
            return (resp.ToJsonString(), 200);
        }
        catch (Exception ex)
        {
            _pfmRunning = false;
            return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400);
        }
    }
#pragma warning restore CS1998

    // ── GET /api/packet-flow/status ───────────────────────────────────────────

    private (string body, int status) HandlePfmStatus()
    {
        var payload = new JsonObject
        {
            ["ok"]        = true,
            ["sessionId"] = _pfmSessionId,
            ["running"]   = _pfmRunning,
            ["result"]    = _pfmFinalResult,
            ["reason"]    = _pfmFinalReason,
            ["tx"]        = StateToJson(_pfmTxSt),
            ["port1"]     = StateToJson(_pfmP1St),
            ["port2"]     = StateToJson(_pfmP2St),
            ["port3"]     = StateToJson(_pfmP3St),
        };
        return (payload.ToJsonString(), 200);
    }

    // ── POST /api/packet-flow/stop ────────────────────────────────────────────

    private (string body, int status) HandlePfmStop()
    {
        _pfm.Stop();
        _pfmRunning = false;
        return ("{\"ok\":true,\"status\":\"stopped\"}", 200);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (string result, string reason) JudgePfm()
    {
        int txM = _pfmTxSt?.MatchedCount    ?? 0;
        int p1M = _pfmP1St?.MatchedCount    ?? 0;
        int p2M = _pfmP2St?.MatchedCount    ?? 0;
        int p3M = _pfmP3St?.MatchedCount    ?? 0;
        int expM = _pfmExpPort == 1 ? p1M : _pfmExpPort == 2 ? p2M : p3M;

        switch (_pfmMode)
        {
            case "TX Only":
                return txM > 0 ? ("PASS", "") : ("FAIL", "TX not observed");

            case "FDB Static Unicast":
            case "Forwarding Check":
                if (_pfmChkTx && txM == 0) return ("FAIL", "TX not observed");
                if (expM == 0) return ("FAIL", $"Expected port {_pfmExpPort} got 0 matched packets");
                if (_pfmChkUnx)
                {
                    if (_pfmExpPort != 1 && p1M > 0) return ("FAIL", "Unexpected forwarding on Port 1");
                    if (_pfmExpPort != 2 && p2M > 0) return ("FAIL", "Unexpected forwarding on Port 2");
                    if (_pfmExpPort != 3 && p3M > 0) return ("FAIL", "Unexpected forwarding on Port 3");
                }
                return ("PASS", "");

            case "Flooding Check":
                if (_pfmChkTx && txM == 0) return ("FAIL", "TX not observed");
                if (_pfmExP1 && p1M == 0) return ("FAIL", "Port 1 got 0 matched packets");
                if (_pfmExP2 && p2M == 0) return ("FAIL", "Port 2 got 0 matched packets");
                if (_pfmExP3 && p3M == 0) return ("FAIL", "Port 3 got 0 matched packets");
                return ("PASS", "");

            default:
                return ("PASS", "");
        }
    }

    private static JsonNode? StateToJson(FlowInterfaceState? st)
    {
        if (st == null) return null;
        return new JsonObject
        {
            ["matched"] = st.MatchedCount,
            ["total"]   = st.TotalCapturedCount,
            ["iface"]   = st.InterfaceName
        };
    }

    // ── POST /api/log/start ───────────────────────────────────────────────────
    private (string body, int status) HandleLogStart(string jsonBody)
    {
        try
        {
            var req    = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var testId = $"packet-flow-{DateTime.Now:yyyyMMddHHmmss}-{Random.Shared.Next(100000, 999999)}";
            var record = new JsonObject { ["testId"] = testId, ["startedAt"] = DateTime.UtcNow.ToString("o"), ["status"] = "running" };
            foreach (var kv in req) record[kv.Key] = kv.Value?.DeepClone();
            lock (_runningTests) _runningTests[testId] = record;
            return (new JsonObject { ["ok"] = true, ["testId"] = testId, ["status"] = "running" }.ToJsonString(), 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    // ── POST /api/log/result ──────────────────────────────────────────────────
    private (string body, int status) HandleLogResult(string jsonBody)
    {
        try
        {
            var req    = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var testId = req["testId"]?.GetValue<string>() ?? $"packet-flow-{DateTime.Now:yyyyMMddHHmmss}-{Random.Shared.Next(100000, 999999)}";
            var record = new JsonObject { ["savedAt"] = DateTime.UtcNow.ToString("o") };
            foreach (var kv in req) record[kv.Key] = kv.Value?.DeepClone();
            var filename = Path.Combine(_testsDir, $"{testId}.json");
            File.WriteAllText(filename, record.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            lock (_runningTests) _runningTests.Remove(testId);
            return (new JsonObject { ["ok"] = true, ["saved"] = true, ["filename"] = Path.GetFileName(filename) }.ToJsonString(), 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    // ── GET /api/log/:testId ──────────────────────────────────────────────────
    private (string body, int status) HandleLogGet(string testId)
    {
        try
        {
            var filePath = Path.Combine(_testsDir, $"{testId}.json");
            if (File.Exists(filePath))
            {
                var data = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject ?? new JsonObject();
                data["ok"] = true;
                return (data.ToJsonString(), 200);
            }
            lock (_runningTests)
            {
                if (_runningTests.TryGetValue(testId, out var running))
                {
                    var r = running.DeepClone() as JsonObject ?? new JsonObject();
                    r["ok"] = true;
                    return (r.ToJsonString(), 200);
                }
            }
            return ("{\"ok\":false,\"error\":\"not found\"}", 404);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    // ── POST /api/log/macro/start ─────────────────────────────────────────────
    private (string body, int status) HandleLogMacroStart(string jsonBody)
    {
        try
        {
            var req     = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var macroId = $"packet-flow-macro-{DateTime.Now:yyyyMMddHHmmss}-{Random.Shared.Next(100000, 999999)}";
            var ports   = req["ports"]?.AsArray()?.Select(n => n?.GetValue<int>() ?? 0).ToList() ?? new List<int> { 1, 2, 3 };
            var steps   = new JsonArray();
            for (int i = 0; i < ports.Count; i++)
                steps.Add(new JsonObject { ["step"] = i + 1, ["flowMode"] = "FDB_STATIC_UNICAST", ["expectedOutputPort"] = ports[i], ["status"] = "pending" });
            var macro = new JsonObject { ["macroId"] = macroId, ["startedAt"] = DateTime.UtcNow.ToString("o"), ["steps"] = steps };
            foreach (var kv in req) if (kv.Key != "ports") macro[kv.Key] = kv.Value?.DeepClone();
            lock (_runningMacros) _runningMacros[macroId] = macro;
            return (new JsonObject { ["ok"] = true, ["macroId"] = macroId, ["steps"] = steps.DeepClone() }.ToJsonString(), 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    // ── POST /api/log/macro/:macroId/step ────────────────────────────────────
    private (string body, int status) HandleLogMacroStep(string macroId, string jsonBody)
    {
        try
        {
            var req     = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            JsonObject? macro;
            lock (_runningMacros)
                _runningMacros.TryGetValue(macroId, out macro);
            macro ??= new JsonObject { ["macroId"] = macroId };

            // Update step in macro
            var steps = macro["steps"]?.AsArray();
            int stepNum = req["step"]?.GetValue<int>() ?? 0;
            var stepNode = steps?.FirstOrDefault(s => s?["step"]?.GetValue<int>() == stepNum) as JsonObject;
            if (stepNode != null)
            {
                stepNode["status"] = req["result"]?.GetValue<string>() == "PASS" ? "pass" : "fail";
                stepNode["result"] = req.DeepClone();
            }

            var filename = Path.Combine(_macrosDir, $"{macroId}.json");
            File.WriteAllText(filename, macro.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return (new JsonObject { ["ok"] = true, ["saved"] = true }.ToJsonString(), 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    // ── GET /api/logs ─────────────────────────────────────────────────────────
    private (string body, int status) HandleLogList()
    {
        try
        {
            JsonArray ReadDir(string dir)
            {
                var arr = new JsonArray();
                if (!Directory.Exists(dir)) return arr;
                foreach (var f in Directory.GetFiles(dir, "*.json").OrderByDescending(f => f).Take(50))
                {
                    try { arr.Add(JsonNode.Parse(File.ReadAllText(f))); }
                    catch { arr.Add(new JsonObject { ["file"] = Path.GetFileName(f), ["error"] = "parse error" }); }
                }
                return arr;
            }
            var payload = new JsonObject { ["ok"] = true, ["tests"] = ReadDir(_testsDir), ["macros"] = ReadDir(_macrosDir) };
            return (payload.ToJsonString(), 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    // ── POST /api/auto/run ────────────────────────────────────────────────────
    // body: {"test":"tx-sanity"|"fdb-test"|"flood-check"}
    // 즉시 반환, 실행은 UI 스레드에서 비동기 실행됨 → GET /api/auto/status 로 폴링
#pragma warning disable CS1998
    private async Task<(string body, int status)> HandleAutoRunAsync(string jsonBody)
    {
        if (AutomationVm == null)
            return ("{\"ok\":false,\"error\":\"Automation not available — app not fully loaded\"}", 503);
        try
        {
            var req  = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var test = req["test"]?.GetValue<string>() ?? string.Empty;
            if (test is not ("tx-sanity" or "fdb-test" or "flood-check"))
                return ("{\"ok\":false,\"error\":\"test must be one of: tx-sanity, fdb-test, flood-check\"}", 400);
            if (AutomationVm.IsRunning)
                return ("{\"ok\":false,\"error\":\"already running\"}", 409);

            var vm = AutomationVm;
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await vm.RunTestAsync(test);
            });

            var resp = new JsonObject { ["ok"] = true, ["test"] = test, ["status"] = "started" };
            return (resp.ToJsonString(), 200);
        }
        catch (Exception ex)
        {
            return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400);
        }
    }
#pragma warning restore CS1998

    // ── GET /api/auto/status ──────────────────────────────────────────────────
    private (string body, int status) HandleAutoStatus()
    {
        if (AutomationVm == null)
            return ("{\"ok\":false,\"error\":\"Automation not available\"}", 503);

        bool   running    = false;
        string result     = string.Empty;
        string reason     = string.Empty;
        string statusText = string.Empty;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            running    = AutomationVm.IsRunning;
            result     = AutomationVm.FinalResult;
            reason     = AutomationVm.FinalReason;
            statusText = AutomationVm.StatusText;
        });

        var payload = new JsonObject
        {
            ["ok"]        = true,
            ["running"]   = running,
            ["result"]    = result,
            ["reason"]    = reason,
            ["statusText"] = statusText
        };
        return (payload.ToJsonString(), 200);
    }

    // ── GET /api/auto/results ─────────────────────────────────────────────────
    private (string body, int status) HandleAutoResults()
    {
        if (AutomationVm == null)
            return ("{\"ok\":false,\"error\":\"Automation not available\"}", 503);

        List<EthernetPacketGenerator.Models.PacketFlowAutoTestRow> rows = new();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            rows = AutomationVm.GetResultsSnapshot();
        });

        var arr = new JsonArray();
        foreach (var r in rows)
        {
            arr.Add(new JsonObject
            {
                ["step"]         = r.Step,
                ["testType"]     = r.TestType,
                ["expectedMode"] = r.ExpectedMode,
                ["expectedPort"] = r.ExpectedPort,
                ["txMatch"]      = r.TxMatch,
                ["port1Match"]   = r.Port1Match,
                ["port2Match"]   = r.Port2Match,
                ["port3Match"]   = r.Port3Match,
                ["result"]       = r.Result,
                ["uploaded"]     = r.Uploaded,
                ["reason"]       = r.Reason
            });
        }
        return (new JsonObject { ["ok"] = true, ["rows"] = arr }.ToJsonString(), 200);
    }

    // ── Static file serving ───────────────────────────────────────────────────
    private (string body, int status) HandleCaptureStatus()
    {
        if (CaptureVm == null)
            return ("{\"ok\":false,\"error\":\"Capture not available\"}", 503);

        bool running = false;
        int total = 0;
        string statusText = string.Empty;
        var ifaceArray = new JsonArray();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            running = CaptureVm.IsCapturing;
            total = CaptureVm.TotalPackets;
            statusText = CaptureVm.StatusText;
            foreach (dynamic i in CaptureVm.GetInterfaceSnapshot())
            {
                ifaceArray.Add(new JsonObject
                {
                    ["name"] = i.Name,
                    ["description"] = i.Description,
                    ["displayName"] = i.DisplayName,
                    ["state"] = i.State,
                    ["selected"] = i.IsSelected
                });
            }
        });

        return (new JsonObject
        {
            ["ok"] = true,
            ["running"] = running,
            ["totalPackets"] = total,
            ["statusText"] = statusText,
            ["interfaces"] = ifaceArray
        }.ToJsonString(), 200);
    }

    private (string body, int status) HandleCaptureStart(string jsonBody)
    {
        if (CaptureVm == null)
            return ("{\"ok\":false,\"error\":\"Capture not available\"}", 503);

        try
        {
            var req = JsonNode.Parse(string.IsNullOrWhiteSpace(jsonBody) ? "{}" : jsonBody) as JsonObject ?? new JsonObject();
            var names = req["interfaces"]?.AsArray()
                .Select(n => n?.GetValue<string>() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList() ?? new List<string>();

            System.Windows.Application.Current.Dispatcher.Invoke(() => CaptureVm.StartCapture(names));
            return ("{\"ok\":true,\"status\":\"started\"}", 200);
        }
        catch (Exception ex)
        {
            return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400);
        }
    }

    private (string body, int status) HandleCaptureStop()
    {
        if (CaptureVm == null)
            return ("{\"ok\":false,\"error\":\"Capture not available\"}", 503);

        System.Windows.Application.Current.Dispatcher.Invoke(() => CaptureVm.StopCapture());
        return ("{\"ok\":true,\"status\":\"stopped\"}", 200);
    }

    private (string body, int status) HandleCaptureClear()
    {
        if (CaptureVm == null)
            return ("{\"ok\":false,\"error\":\"Capture not available\"}", 503);

        System.Windows.Application.Current.Dispatcher.Invoke(() => CaptureVm.ClearCapture());
        return ("{\"ok\":true,\"status\":\"cleared\"}", 200);
    }

    private (string body, int status) HandleCapturePackets(string path)
    {
        if (CaptureVm == null)
            return ("{\"ok\":false,\"error\":\"Capture not available\"}", 503);

        int limit = 500;
        var qIndex = path.IndexOf('?');
        if (qIndex >= 0)
        {
            foreach (var part in path[(qIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("limit", StringComparison.OrdinalIgnoreCase) && int.TryParse(kv[1], out var parsed))
                    limit = Math.Clamp(parsed, 1, 5000);
            }
        }

        List<EthernetPacketGenerator.Models.CaptureRow> rows = new();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            rows = CaptureVm.GetPacketsSnapshot(limit);
        });

        var arr = new JsonArray();
        foreach (var r in rows)
        {
            arr.Add(new JsonObject
            {
                ["no"] = r.No,
                ["time"] = r.Time,
                ["interfaceName"] = r.InterfaceName,
                ["srcMac"] = r.SrcMac,
                ["dstMac"] = r.DstMac,
                ["source"] = r.Source,
                ["destination"] = r.Destination,
                ["protocol"] = r.Protocol,
                ["length"] = r.Length,
                ["info"] = r.Info,
                ["detailText"] = r.DetailText,
                ["hexDump"] = r.HexDump
            });
        }

        return (new JsonObject { ["ok"] = true, ["rows"] = arr }.ToJsonString(), 200);
    }

    private (string body, int status) HandleSerialStatus()
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"Serial terminal not available\"}", 503);

        object snapshot = new();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            snapshot = HyperTerminalVm.GetSnapshot();
        });

        return (System.Text.Json.JsonSerializer.Serialize(new { ok = true, terminal = snapshot }), 200);
    }

    private (string body, int status) HandleSerialConnect(string jsonBody)
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"Serial terminal not available\"}", 503);

        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var port = req["port"]?.GetValue<string>() ?? string.Empty;
            var baud = req["baudRate"]?.GetValue<int>() ?? 115200;
            System.Windows.Application.Current.Dispatcher.Invoke(() => HyperTerminalVm.ConnectForApi(port, baud));
            return ("{\"ok\":true,\"status\":\"connected\"}", 200);
        }
        catch (Exception ex)
        {
            return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400);
        }
    }

    private (string body, int status) HandleSerialDisconnect()
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"Serial terminal not available\"}", 503);

        System.Windows.Application.Current.Dispatcher.Invoke(() => HyperTerminalVm.DisconnectForApi());
        return ("{\"ok\":true,\"status\":\"disconnected\"}", 200);
    }

    private (string body, int status) HandleSerialSend(string jsonBody)
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"Serial terminal not available\"}", 503);

        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var text = req["text"]?.GetValue<string>() ?? string.Empty;
            System.Windows.Application.Current.Dispatcher.Invoke(() => HyperTerminalVm.SendForApi(text));
            return ("{\"ok\":true,\"status\":\"sent\"}", 200);
        }
        catch (Exception ex)
        {
            return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400);
        }
    }

    private (string body, int status) HandleSerialClear()
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"Serial terminal not available\"}", 503);

        System.Windows.Application.Current.Dispatcher.Invoke(() => HyperTerminalVm.ClearForApi());
        return ("{\"ok\":true,\"status\":\"cleared\"}", 200);
    }

    private (string body, int status) HandleRegisterStatus()
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"Register viewer not available\"}", 503);

        string baseAddress = string.Empty;
        bool connected = false;
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            baseAddress = HyperTerminalVm.RegisterViewerVM.BaseAddressHex;
            connected = HyperTerminalVm.IsConnected;
        });

        return (new JsonObject
        {
            ["ok"] = true,
            ["serialConnected"] = connected,
            ["baseAddress"] = baseAddress
        }.ToJsonString(), 200);
    }

    private async Task<(string body, int status)> HandleRegisterReadAsync(string jsonBody)
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"Register viewer not available\"}", 503);
        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var offset = ParseUInt(req["offset"]?.GetValue<string>() ?? "0");
            uint value = 0;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                value = await HyperTerminalVm.RegisterViewerVM.ReadRegisterForApiAsync(offset);
            });
            return (new JsonObject { ["ok"] = true, ["offset"] = $"0x{offset:X}", ["value"] = $"0x{value:X8}", ["valueDec"] = value }.ToJsonString(), 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private async Task<(string body, int status)> HandleRegisterWriteAsync(string jsonBody)
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"Register viewer not available\"}", 503);
        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var offset = ParseUInt(req["offset"]?.GetValue<string>() ?? "0");
            var value = ParseUInt(req["value"]?.GetValue<string>() ?? "0");
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await HyperTerminalVm.RegisterViewerVM.WriteRegisterForApiAsync(offset, value);
            });
            return ("{\"ok\":true,\"status\":\"written\"}", 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private async Task<(string body, int status)> HandleFdbReadAsync(string jsonBody)
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"FDB not available\"}", 503);
        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var mac = req["mac"]?.GetValue<string>() ?? string.Empty;
            var vlanValid = req["vlanValid"]?.GetValue<bool>() ?? false;
            var vlanId = req["vlanId"]?.GetValue<int>() ?? 0;
            object? entry = null;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                entry = await HyperTerminalVm.RegisterViewerVM.FdbReadByMacForApiAsync(mac, vlanValid, vlanId);
            });
            return (System.Text.Json.JsonSerializer.Serialize(new { ok = true, found = entry != null, entry }), 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private async Task<(string body, int status)> HandleFdbWriteAsync(string jsonBody)
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"FDB not available\"}", 503);
        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var mac = req["mac"]?.GetValue<string>() ?? string.Empty;
            var vlanValid = req["vlanValid"]?.GetValue<bool>() ?? false;
            var vlanId = req["vlanId"]?.GetValue<int>() ?? 0;
            var port = req["port"]?.GetValue<int>() ?? 1;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await HyperTerminalVm.RegisterViewerVM.FdbWriteByMacForApiAsync(mac, vlanValid, vlanId, port);
            });
            return ("{\"ok\":true,\"status\":\"fdb-written\"}", 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private async Task<(string body, int status)> HandleFdbDeleteAsync(string jsonBody)
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"FDB not available\"}", 503);
        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var mac = req["mac"]?.GetValue<string>() ?? string.Empty;
            var vlanValid = req["vlanValid"]?.GetValue<bool>() ?? false;
            var vlanId = req["vlanId"]?.GetValue<int>() ?? 0;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await HyperTerminalVm.RegisterViewerVM.FdbDeleteByMacForApiAsync(mac, vlanValid, vlanId);
            });
            return ("{\"ok\":true,\"status\":\"fdb-deleted\"}", 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private async Task<(string body, int status)> HandleFdbFlushAsync()
    {
        if (HyperTerminalVm == null)
            return ("{\"ok\":false,\"error\":\"FDB not available\"}", 503);
        try
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await HyperTerminalVm.RegisterViewerVM.FdbFlushForApiAsync();
            });
            return ("{\"ok\":true,\"status\":\"fdb-flushed\"}", 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private static uint ParseUInt(string value)
    {
        var text = value.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(text[2..], 16)
            : Convert.ToUInt32(text, 10);
    }

    private (string body, int status) HandleAppStatus()
    {
        if (MainVm == null)
            return ("{\"ok\":false,\"error\":\"App not available\"}", 503);

        object payload = new();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            payload = new
            {
                ok = true,
                selectedTabIndex = MainVm.SelectedTabIndex,
                packetCount = MainVm.PacketListVM.Sequence.Count,
                capture = new { MainVm.CaptureVM.IsCapturing, MainVm.CaptureVM.TotalPackets },
                serial = new { MainVm.HyperTerminalVM.IsConnected, MainVm.HyperTerminalVM.ConnectionStatus }
            };
        });

        return (System.Text.Json.JsonSerializer.Serialize(payload), 200);
    }

    private (string body, int status) HandleTestCasesStatus()
    {
        if (MainVm == null)
            return ("{\"ok\":false,\"error\":\"Test cases not available\"}", 503);

        object snapshot = new();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            snapshot = MainVm.TestCaseMgrVM.GetSnapshotForApi();
        });
        return (System.Text.Json.JsonSerializer.Serialize(new { ok = true, testCases = snapshot }), 200);
    }

    private (string body, int status) HandleTestCasesAddGroup(string jsonBody)
    {
        if (MainVm == null)
            return ("{\"ok\":false,\"error\":\"Test cases not available\"}", 503);
        try
        {
            var req = JsonNode.Parse(string.IsNullOrWhiteSpace(jsonBody) ? "{}" : jsonBody) as JsonObject ?? new JsonObject();
            var name = req["name"]?.GetValue<string>();
            System.Windows.Application.Current.Dispatcher.Invoke(() => MainVm.TestCaseMgrVM.AddGroupForApi(name));
            return ("{\"ok\":true,\"status\":\"group-added\"}", 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private (string body, int status) HandleTestCasesAdd(string jsonBody)
    {
        if (MainVm == null)
            return ("{\"ok\":false,\"error\":\"Test cases not available\"}", 503);
        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var groupIndex = req["groupIndex"]?.GetValue<int>() ?? 0;
            var name = req["name"]?.GetValue<string>();
            System.Windows.Application.Current.Dispatcher.Invoke(() => MainVm.TestCaseMgrVM.AddTestCaseForApi(groupIndex, name));
            return ("{\"ok\":true,\"status\":\"testcase-added\"}", 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private (string body, int status) HandleTestCasesSelect(string jsonBody)
    {
        if (MainVm == null)
            return ("{\"ok\":false,\"error\":\"Test cases not available\"}", 503);
        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var groupIndex = req["groupIndex"]?.GetValue<int>() ?? 0;
            var testCaseIndex = req["testCaseIndex"]?.GetValue<int>() ?? 0;
            System.Windows.Application.Current.Dispatcher.Invoke(() => MainVm.TestCaseMgrVM.SelectTestCaseForApi(groupIndex, testCaseIndex));
            return ("{\"ok\":true,\"status\":\"testcase-selected\"}", 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private (string body, int status) HandleTestCasesSaveCurrent()
    {
        if (MainVm == null)
            return ("{\"ok\":false,\"error\":\"Test cases not available\"}", 503);
        try
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => MainVm.TestCaseMgrVM.SaveCurrentToSelectedForApi());
            return ("{\"ok\":true,\"status\":\"current-saved\"}", 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private (string body, int status) HandleTestCasesDelete(string jsonBody)
    {
        if (MainVm == null)
            return ("{\"ok\":false,\"error\":\"Test cases not available\"}", 503);
        try
        {
            var req = JsonNode.Parse(jsonBody) as JsonObject ?? new JsonObject();
            var groupIndex = req["groupIndex"]?.GetValue<int>() ?? 0;
            if (req["testCaseIndex"] is JsonNode tcNode)
            {
                var testCaseIndex = tcNode.GetValue<int>();
                System.Windows.Application.Current.Dispatcher.Invoke(() => MainVm.TestCaseMgrVM.DeleteTestCaseForApi(groupIndex, testCaseIndex));
            }
            else
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => MainVm.TestCaseMgrVM.DeleteGroupForApi(groupIndex));
            }
            return ("{\"ok\":true,\"status\":\"deleted\"}", 200);
        }
        catch (Exception ex) { return ($"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}", 400); }
    }

    private (string body, int status) HandleSequenceStatus()
    {
        if (MainVm == null)
            return ("{\"ok\":false,\"error\":\"Sequence not available\"}", 503);

        object payload = new();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            payload = new
            {
                ok = true,
                count = MainVm.PacketListVM.Sequence.Count,
                items = MainVm.PacketListVM.Sequence.Select(s => new
                {
                    s.Index,
                    kind = s.Kind.ToString(),
                    s.IsChecked,
                    name = s.DisplayName,
                    source = s.DisplaySrcMac,
                    destination = s.DisplayDstMac,
                    protocol = s.DisplayProtocol,
                    description = s.DisplayDescription,
                    iface = s.DisplayInterface
                }).ToList()
            };
        });

        return (System.Text.Json.JsonSerializer.Serialize(payload), 200);
    }

    private async Task ServeStaticAsync(NetworkStream stream, string urlPath)
    {
        string filePath;
        if (urlPath == "/" || urlPath == "" || urlPath.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
        }
        else
        {
            var safe = urlPath.TrimStart('/')
                               .Replace('/', Path.DirectorySeparatorChar)
                               .Replace("..", string.Empty); // 경로 탐색 방지
            filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", safe);
        }

        byte[] content;
        int    status;
        string contentType;

        if (File.Exists(filePath))
        {
            content     = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            contentType = GetContentType(Path.GetExtension(filePath));
            status      = 200;
        }
        else
        {
            content     = Encoding.UTF8.GetBytes("404 Not Found");
            contentType = "text/plain; charset=utf-8";
            status      = 404;
        }

        var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {content.Length}\r\n" +
                     $"Access-Control-Allow-Origin: *\r\n" +
                     $"Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
        await stream.WriteAsync(content).ConfigureAwait(false);
    }

    private static string GetContentType(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js"   => "application/javascript; charset=utf-8",
        ".css"  => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".ico"  => "image/x-icon",
        ".png"  => "image/png",
        ".svg"  => "image/svg+xml",
        _       => "application/octet-stream"
    };

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
