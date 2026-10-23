using Ledajans.Shared;

namespace Ledajans.Server.Services;

public static class ExcusedDayRules
{
    public readonly record struct Day(DateOnly Date, string? UserId, string Type);

    public static HashSet<string> ForDate(
        DateOnly date,
        IReadOnlyCollection<string> activeEmployeeIds,
        IEnumerable<Day> days)
    {
        var todays = days.Where(d => d.Date == date).ToList();
        if (todays.Any(d => d.UserId is null && d.Type == NonWorkingDayTypes.Holiday))
            return activeEmployeeIds.ToHashSet();

        var excused = new HashSet<string>();
        foreach (var day in todays)
        {
            if (day.UserId is not null)
                excused.Add(day.UserId);
        }

        return excused;
    }
}
