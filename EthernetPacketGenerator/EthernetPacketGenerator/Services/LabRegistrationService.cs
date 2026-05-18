using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpPcap;

namespace EthernetPacketGenerator.Services;

/// <summary>
/// 리시버 PC의 Node.js 서버(POST /api/register-node)에 이 PC의
/// 인터페이스 정보와 현재 선택된 인터페이스를 등록한다.
///
/// 설정 파일: 실행 파일 옆에 lab-config.json 을 만든다.
/// {
///   "receiverUrl": "http://192.168.x.x:8080",
///   "senderUrl":   "http://192.168.x.y:8080"
/// }
/// (환경변수 LAB_RECEIVER / LAB_SENDER 로도 설정 가능 — 파일이 우선)
/// </summary>
public static class LabRegistrationService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // 설정 캐시 (파일/env 재읽기 비용 제거)
    private static string? _receiverUrl;
    private static string? _senderUrl;

    /// <summary>
    /// 현재 선택된 SharpPcap 디바이스를 포함해 리시버에 등록한다.
    /// SendViewModel.SelectedInterface 가 바뀔 때마다, 그리고 타이머에서 호출한다.
    /// </summary>
    public static async Task RegisterAsync(ILiveDevice? selectedDevice = null)
    {
        LoadConfig();
        if (string.IsNullOrWhiteSpace(_receiverUrl) || string.IsNullOrWhiteSpace(_senderUrl))
            return;

        var selectedMac  = GetMacBytes(selectedDevice);
        var (interfaces, selectedName) = BuildInterfaceList(selectedMac, selectedDevice);

        var payload = new JsonObject
        {
            ["url"]               = _senderUrl.TrimEnd('/'),
            ["interfaces"]        = interfaces,
            ["selectedInterface"] = selectedName
        };

        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{_receiverUrl.TrimEnd('/')}/api/register-node", content);
        response.EnsureSuccessStatusCode();
    }

    // ── Config ────────────────────────────────────────────────────────────────

    private static void LoadConfig()
    {
        // 이미 로드됐으면 스킵 (변경 시 앱 재시작으로 반영)
        if (_receiverUrl != null) return;

        // 1) 실행 파일 옆 lab-config.json
        var configPath = Path.Combine(AppContext.BaseDirectory, "lab-config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(configPath));
                _receiverUrl = json?["receiverUrl"]?.GetValue<string>()?.Trim().TrimEnd('/');
                _senderUrl   = json?["senderUrl"]?.GetValue<string>()?.Trim().TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(_receiverUrl) && !string.IsNullOrWhiteSpace(_senderUrl))
                    return;
            }
            catch { }
        }

        // 2) 환경변수 폴백
        _receiverUrl = Environment.GetEnvironmentVariable("LAB_RECEIVER")?.Trim().TrimEnd('/');
        _senderUrl   = Environment.GetEnvironmentVariable("LAB_SENDER")?.Trim().TrimEnd('/');
    }

    // ── MAC helpers ───────────────────────────────────────────────────────────

    private static byte[]? GetMacBytes(ILiveDevice? dev)
    {
        if (dev == null) return null;
        try
        {
            var b = dev.MacAddress?.GetAddressBytes();
            return b?.Length == 6 ? b : null;
        }
        catch { return null; }
    }

    private static string FormatMac(byte[] bytes)
        => string.Join(":", bytes.Select(b => b.ToString("x2")));

    // ── Interface list ────────────────────────────────────────────────────────

    private static (JsonArray interfaces, string? selectedName) BuildInterfaceList(
        byte[]? selectedMac, ILiveDevice? selectedDevice)
    {
        var arr     = new JsonArray();
        string? sel = null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var macBytes = nic.GetPhysicalAddress().GetAddressBytes();
            if (macBytes.Length != 6) continue;

            var mac   = FormatMac(macBytes);
            var state = nic.OperationalStatus == OperationalStatus.Up ? "up" : "down";

            int mtu = 1500;
            try { mtu = nic.GetIPProperties().GetIPv4Properties()?.Mtu ?? 1500; }
            catch { }

            // IPv4 목록 — 호출 시점의 실시간 값
            var ipv4 = new JsonArray();
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    continue;
                ipv4.Add(new JsonObject
                {
                    ["local"]     = ua.Address.ToString(),
                    ["prefixlen"] = ua.PrefixLength
                });
            }

            arr.Add(new JsonObject
            {
                ["name"]  = nic.Name,
                ["mac"]   = mac,
                ["state"] = state,
                ["mtu"]   = mtu,
                ["ipv4"]  = ipv4
            });

            // 선택된 디바이스를 MAC으로 매칭
            if (selectedMac != null && macBytes.SequenceEqual(selectedMac))
                sel = nic.Name;
        }

        // MAC 매칭 실패 시: SharpPcap Description 에서 정리된 이름으로 폴백
        if (sel == null && selectedDevice != null)
            sel = GetFallbackName(selectedDevice);

        return (arr, sel);
    }

    // SharpPcap Description에서 {GUID} 부분을 제거한 사람이 읽기 좋은 이름
    private static string? GetFallbackName(ILiveDevice dev)
    {
        var desc = dev.Description ?? dev.Name ?? string.Empty;
        var idx  = desc.LastIndexOf('{');
        if (idx > 0) desc = desc[..idx].TrimEnd(' ', '\\', '_');
        return desc.Length > 0 ? desc : dev.Name;
    }
}
