using Microsoft.JSInterop;

namespace Ledajans.Client.Services;

public class DeviceService
{
    private readonly IJSRuntime _js;

    public DeviceService(IJSRuntime js) => _js = js;

    public async Task<string?> GetDeviceIdAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("ledajansDevice.getOrCreateId");
        }
        catch (JSException)
        {
            return null;
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
    }
}
