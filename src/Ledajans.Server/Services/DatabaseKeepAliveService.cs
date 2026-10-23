using Ledajans.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Ledajans.Server.Services;

public sealed class DatabaseKeepAliveService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DatabaseKeepAliveService> _logger;

    public DatabaseKeepAliveService(
        IServiceScopeFactory scopes,
        IHostEnvironment environment,
        ILogger<DatabaseKeepAliveService> logger)
    {
        _scopes = scopes;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken))
            return;

        var announced = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var healthy = false;
            Exception? failure = null;
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                healthy = await db.Database.CanConnectAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (healthy)
            {
                announced = false;
            }
            else if (!announced)
            {
                announced = true;
                MySqlResilience.ClearPools();
                _logger.LogWarning(failure, "Veritabani baglantisi koptu, havuz temizlendi.");
                ServerFaultLog.Write(
                    _environment.ContentRootPath,
                    "db-heal.txt",
                    failure?.ToString() ?? "CanConnect false");
            }

            if (!await DelayAsync(TimeSpan.FromMinutes(2), stoppingToken))
                return;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
