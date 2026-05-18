using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Services;

/// <summary>
/// 8080 LabApiServer의 로그 추적 엔드포인트 클라이언트.
/// 별도 Node 서버 없이 WPF 내장 서버만 사용.
/// </summary>
public class ManagerServerClient
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string BaseUrl { get; set; } = "http://localhost:8080";

    public async Task<bool> CheckHealthAsync()
    {
        try
        {
            var resp = await _http.GetAsync($"{BaseUrl}/api/health");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<ManagerHealthResponse?> GetHealthAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<ManagerHealthResponse>($"{BaseUrl}/api/health", _json);
        }
        catch { return null; }
    }

    /// <summary>로그 세션 시작 → testId 반환</summary>
    public async Task<PacketFlowStartResponse?> StartPacketFlowAsync(PacketFlowStartRequest request)
    {
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(request, _json), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BaseUrl}/api/log/start", body);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<PacketFlowStartResponse>(_json);
        }
        catch { return null; }
    }

    /// <summary>테스트 결과 저장 → logs/tests/*.json</summary>
    public async Task<bool> UploadPacketFlowResultAsync(PacketFlowResultRequest request)
    {
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(request, _json), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BaseUrl}/api/log/result", body);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>매크로 세션 시작 → macroId + steps 반환</summary>
    public async Task<PacketFlowMacroStartResponse?> StartPacketFlowMacroAsync(PacketFlowMacroStartRequest request)
    {
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(request, _json), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BaseUrl}/api/log/macro/start", body);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<PacketFlowMacroStartResponse>(_json);
        }
        catch { return null; }
    }

    /// <summary>매크로 스텝 결과 저장 → logs/macros/*.json 업데이트</summary>
    public async Task<bool> UploadMacroStepResultAsync(string macroId, PacketFlowMacroStepResultRequest request)
    {
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(request, _json), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{BaseUrl}/api/log/macro/{macroId}/step", body);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>전체 로그 목록 (tests + macros)</summary>
    public async Task<string> GetLogsJsonAsync()
    {
        try
        {
            return await _http.GetStringAsync($"{BaseUrl}/api/logs");
        }
        catch (Exception ex) { return $"{{\"error\":\"{ex.Message}\"}}"; }
    }
}
