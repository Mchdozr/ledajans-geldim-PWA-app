using Ledajans.Server.Services;
using Ledajans.Shared;
using Xunit;

namespace Ledajans.Tests;

public class ResilienceRulesTests
{
    [Fact]
    public void Apply_keeps_database_and_password_and_limits_pool()
    {
        var result = MySqlResilience.Apply(
            "Server=localhost;Port=3306;Database=LedajansDb;User=geldim;Password=secret;");

        Assert.Contains("LedajansDb", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret", result, StringComparison.Ordinal);
        Assert.Contains("Maximum Pool Size=20", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Keep Alive=30", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection Idle Timeout=30", result, StringComparison.OrdinalIgnoreCase);

        var second = MySqlResilience.Apply(result);
        Assert.Contains("Maximum Pool Size=20", second, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_leaves_blank_connection_string_alone()
    {
        Assert.Equal("", MySqlResilience.Apply(""));
    }

    [Fact]
    public void Pool_poison_detects_dropped_mysql_not_business_errors()
    {
        Assert.True(MySqlResilience.IsPoolPoisoning(new TimeoutException("Connect Timeout expired.")));
        Assert.True(MySqlResilience.IsPoolPoisoning(new Exception("MySQL server has gone away")));
        Assert.True(MySqlResilience.IsPoolPoisoning(new InvalidOperationException("outer", new Exception("Too many connections"))));
        Assert.False(MySqlResilience.IsPoolPoisoning(new InvalidOperationException("Kurum seçimi gerekli.")));
    }

    [Fact]
    public void Company_holiday_excuses_every_active_employee()
    {
        var date = new DateOnly(2026, 10, 29);
        var excused = ExcusedDayRules.ForDate(
            date,
            ["ada", "can"],
            [new ExcusedDayRules.Day(date, null, NonWorkingDayTypes.Holiday)]);

        Assert.Contains("ada", excused);
        Assert.Contains("can", excused);
    }

    [Fact]
    public void Personal_leave_excuses_only_that_user()
    {
        var date = new DateOnly(2026, 10, 29);
        var excused = ExcusedDayRules.ForDate(
            date,
            ["ada", "can"],
            [new ExcusedDayRules.Day(date, "ada", NonWorkingDayTypes.Leave)]);

        Assert.Contains("ada", excused);
        Assert.DoesNotContain("can", excused);
    }

    [Fact]
    public void Other_dates_do_not_excuse_anyone()
    {
        var date = new DateOnly(2026, 10, 29);
        var excused = ExcusedDayRules.ForDate(
            date,
            ["ada"],
            [new ExcusedDayRules.Day(date.AddDays(1), null, NonWorkingDayTypes.Holiday)]);

        Assert.Empty(excused);
    }
}
