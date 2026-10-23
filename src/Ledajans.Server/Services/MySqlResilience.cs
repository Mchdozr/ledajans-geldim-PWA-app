using MySqlConnector;

namespace Ledajans.Server.Services;

public static class MySqlResilience
{
    public static string Apply(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        var hasMax = connectionString.Contains("MaximumPoolSize", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Max Pool Size", StringComparison.OrdinalIgnoreCase);

        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            ConnectionReset = true,
            MinimumPoolSize = 0,
            ConnectionIdleTimeout = 30,
            Keepalive = 30,
            DefaultCommandTimeout = 60,
            ConnectionTimeout = 15
        };

        if (!hasMax)
            builder.MaximumPoolSize = 20;

        return builder.ConnectionString;
    }

    public static bool IsPoolPoisoning(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;

            if (current is MySqlException mysql && IsConnectionError((int)mysql.ErrorCode))
                return true;

            if (current.Message.Contains("gone away", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("Too many connections", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("Connect Timeout", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("Connection reset", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static void ClearPools() => MySqlConnection.ClearAllPools();

    private static bool IsConnectionError(int code) => code is 1040 or 1042 or 1043 or 2006 or 2013;
}
