using System.Net.Http.Json;
using Ledajans.Shared;

namespace Ledajans.Client.Services;

public class AttendanceService
{
    private readonly HttpClient _http;

    public AttendanceService(HttpClient http) => _http = http;

    public async Task<TodayStatusResponse> GetTodayAsync()
    {
        var response = await _http.GetAsync("api/attendance/today");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Durum alınamadı ({(int)response.StatusCode})");
        return await response.Content.ReadFromJsonAsync<TodayStatusResponse>() ?? new();
    }

    public async Task<CheckInResponse> CheckInAsync(CheckInRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/attendance/checkin", request);
        if (!response.IsSuccessStatusCode)
            return new CheckInResponse { Success = false, Message = await HttpResponseHelper.ToUserMessageAsync(response) };
        return await response.Content.ReadFromJsonAsync<CheckInResponse>()
            ?? new CheckInResponse { Success = false, Message = "Sunucu hatası." };
    }

    public async Task<CheckOutResponse> CheckOutAsync(CheckInRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/attendance/checkout", request);
        if (!response.IsSuccessStatusCode)
            return new CheckOutResponse { Success = false, Message = await HttpResponseHelper.ToUserMessageAsync(response) };
        return await response.Content.ReadFromJsonAsync<CheckOutResponse>()
            ?? new CheckOutResponse { Success = false, Message = "Sunucu hatası." };
    }

    public async Task<List<MyAttendanceHistoryItem>> GetMyHistoryAsync(int limit = 100)
        => await _http.GetJsonOrDefaultAsync<List<MyAttendanceHistoryItem>>($"api/attendance/history?limit={limit}") ?? new();

    public async Task<AttendanceReportItem?> ManualCheckInAsync(ManualCheckInRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/attendance/manual", request);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AttendanceReportItem>();
    }

    public async Task<AttendanceReportItem?> UpdateCheckInTimeAsync(int id, TimeOnly localTime)
    {
        var response = await _http.PutAsJsonAsync($"api/attendance/{id}", new UpdateCheckInTimeRequest { LocalTime = localTime });
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AttendanceReportItem>();
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var response = await _http.DeleteAsync($"api/attendance/{id}");
        return response.IsSuccessStatusCode;
    }
}
