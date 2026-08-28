using System.Net.Http.Json;
using Ledajans.Client.Auth;
using Ledajans.Shared;

namespace Ledajans.Client.Services;

public class AuthService
{
    private readonly HttpClient _http;
    private readonly AuthStateProvider _authState;
    private readonly DeviceService _deviceService;

    public AuthService(HttpClient http, AuthStateProvider authState, DeviceService deviceService)
    {
        _http = http;
        _authState = authState;
        _deviceService = deviceService;
    }

    public async Task<string?> LoginAsync(LoginRequest request)
    {
        request.DeviceId = await _deviceService.GetDeviceIdAsync();
        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return "Cihaz tanımlanamadı. Sayfayı yenileyip tekrar deneyin (önbelleği temizlemeniz gerekebilir).";
        }

        var response = await _http.PostAsJsonAsync("api/auth/login", request);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var err = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                return err?.Message ?? "Giriş başarısız.";
            }
            catch
            {
                return "Giriş başarısız.";
            }
        }

        var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
        if (result is null) return "Giriş başarısız.";

        await _authState.NotifyLogin(result.Token);
        return null;
    }

    public Task LogoutAsync() => _authState.NotifyLogout();

    private record ErrorResponse(string Message);
}
