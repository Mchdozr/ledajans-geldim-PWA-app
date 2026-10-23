using System.Globalization;
using System.Text;
using Ledajans.Server.Data;
using Ledajans.Server.Services;
using Ledajans.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledajans.Server.Controllers;

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/[controller]")]
public class ReportsController : ControllerBase
{
    private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");

    private readonly AppDbContext _db;
    private readonly IAttendancePolicyService _policy;
    private readonly ILocationScope _locationScope;

    public ReportsController(AppDbContext db, IAttendancePolicyService policy, ILocationScope locationScope)
    {
        _db = db;
        _policy = policy;
        _locationScope = locationScope;
    }

    private async Task<ActionResult<int>> RequireLocationIdAsync()
    {
        var locationId = await _locationScope.GetAdminLocationIdAsync();
        if (locationId is null)
            return BadRequest(new { message = "Kurum seçimi gerekli." });
        return locationId.Value;
    }

    [HttpGet("today-summary")]
    public async Task<ActionResult<TodaySummaryResponse>> TodaySummary([FromQuery] string? department)
    {
        var locationResult = await RequireLocationIdAsync();
        if (locationResult.Result is not null) return locationResult.Result;
        return Ok(await BuildSummaryForDateAsync(AppTime.Today, locationResult.Value!, department));
    }

    [HttpGet]
    public async Task<ActionResult<List<AttendanceReportItem>>> Get(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? userId, [FromQuery] string? department)
    {
        var locationResult = await RequireLocationIdAsync();
        if (locationResult.Result is not null) return locationResult.Result;
        return Ok(await QueryAsync(locationResult.Value!, from, to, userId, department));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? userId, [FromQuery] string? department)
    {
        var locationResult = await RequireLocationIdAsync();
        if (locationResult.Result is not null) return locationResult.Result;

        var items = await QueryAsync(locationResult.Value!, from, to, userId, department);
        var sb = new StringBuilder();
        sb.AppendLine("Tarih;Departman;Kullanici;AdSoyad;GirisSaati(TR);CikisSaati(TR);GecKaldi;Uzaklik(m);Not");
        foreach (var i in items)
        {
            var localTime = AppTime.ToTurkey(i.CheckInUtc);
            var checkOutTime = i.CheckOutUtc is not null
                ? AppTime.ToTurkey(i.CheckOutUtc.Value).ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : "";
            sb.AppendLine(string.Join(';',
                i.LocalDate.ToString("yyyy-MM-dd"),
                Csv(i.Department),
                Csv(i.UserName),
                Csv(i.FullName),
                localTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                checkOutTime,
                i.IsLate ? "Evet" : "Hayir",
                i.DistanceMeters.ToString(CultureInfo.InvariantCulture),
                Csv(i.AdminNote)));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"yoklama_{AppTime.Today:yyyyMMdd}.csv");
    }

    [HttpGet("export-absent")]
    public async Task<IActionResult> ExportAbsent(
        [FromQuery] DateOnly? date,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? department)
    {
        var locationResult = await RequireLocationIdAsync();
        if (locationResult.Result is not null) return locationResult.Result;
        var locationId = locationResult.Value!;

        if (from is not null && to is not null)
        {
            if (from > to)
                return BadRequest(new { message = "Başlangıç tarihi bitişten büyük olamaz." });

            var days = (to.Value.DayNumber - from.Value.DayNumber) + 1;
            if (days > 93)
                return BadRequest(new { message = "En fazla 93 günlük dönem seçilebilir." });

            var csv = await BuildPeriodAbsentCsvAsync(locationId, from.Value, to.Value, department);
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            return File(bytes, "text/csv", $"gelmeyenler_{from:yyyyMMdd}_{to:yyyyMMdd}.csv");
        }

        var targetDate = date ?? AppTime.Today;
        var summary = await BuildSummaryForDateAsync(targetDate, locationId, department);
        var singleCsv = BuildSingleDayAbsentCsv(summary);
        var singleBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(singleCsv)).ToArray();
        return File(singleBytes, "text/csv", $"gelmeyenler_{targetDate:yyyyMMdd}.csv");
    }

    private async Task<string> BuildPeriodAbsentCsvAsync(int locationId, DateOnly from, DateOnly to, string? department)
    {
        var sb = new StringBuilder();
        var createdAt = AppTime.ToTurkey(DateTime.UtcNow);
        var employees = await GetActiveEmployeesAsync(locationId, department);
        var totalDays = to.DayNumber - from.DayNumber + 1;

        var presentByDate = await _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.LocationId == locationId && a.LocalDate >= from && a.LocalDate <= to)
            .Select(a => new { a.LocalDate, a.UserId })
            .ToListAsync();
        var presentSet = presentByDate.Select(x => (x.LocalDate, x.UserId)).ToHashSet();

        var daysOffRows = await _db.NonWorkingDays.AsNoTracking()
            .Where(n => n.LocationId == locationId && n.Date >= from && n.Date <= to)
            .Select(n => new { n.Date, n.UserId, n.Type })
            .ToListAsync();
        var daysOff = daysOffRows
            .Select(n => new ExcusedDayRules.Day(n.Date, n.UserId, n.Type))
            .ToList();
        var employeeIds = employees.Select(e => e.UserId).ToList();

        var rows = new List<(DateOnly Date, TodaySummaryEmployee Employee)>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var excused = ExcusedDayRules.ForDate(d, employeeIds, daysOff);
            foreach (var emp in employees)
            {
                if (presentSet.Contains((d, emp.UserId))) continue;
                if (excused.Contains(emp.UserId)) continue;
                rows.Add((d, emp));
            }
        }

        sb.AppendLine("LEDAJANS GELDIM - DONEM GELMEYENLER RAPORU");
        sb.AppendLine($"Donem Baslangic;{from:yyyy-MM-dd}");
        sb.AppendLine($"Donem Bitis;{to:yyyy-MM-dd}");
        sb.AppendLine($"Departman;{Csv(department ?? "Tumu")}");
        sb.AppendLine($"Gun Sayisi;{totalDays}");
        sb.AppendLine($"Aktif Calisan Sayisi;{employees.Count}");
        sb.AppendLine($"Toplam Gelmeme Kaydi;{rows.Count}");
        sb.AppendLine($"Rapor Olusturma;{createdAt:dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("Sira;Tarih;Gun;Departman;Kullanici Adi;Ad Soyad;E-posta;Durum;Aciklama");

        var index = 1;
        foreach (var (date, emp) in rows.OrderBy(r => r.Date).ThenBy(r => r.Employee.FullName))
        {
            sb.AppendLine(string.Join(';',
                index++,
                date.ToString("yyyy-MM-dd"),
                Csv(DayName(date)),
                Csv(emp.Department),
                Csv(emp.UserName),
                Csv(emp.FullName),
                Csv(emp.Email ?? ""),
                "Gelmedi",
                "Giris kaydi yok"));
        }

        return sb.ToString();
    }

    private static string BuildSingleDayAbsentCsv(TodaySummaryResponse summary)
    {
        var sb = new StringBuilder();
        var createdAt = AppTime.ToTurkey(DateTime.UtcNow);
        var expected = summary.TotalActive - summary.ExcusedCount;
        var rate = expected > 0
            ? Math.Round(summary.PresentCount * 100.0 / expected, 1)
            : 0;

        sb.AppendLine("LEDAJANS GELDIM - GUNLUK GELMEYENLER RAPORU");
        sb.AppendLine($"Yoklama Tarihi;{summary.Date:yyyy-MM-dd}");
        sb.AppendLine($"Gun;{Csv(DayName(summary.Date))}");
        sb.AppendLine($"Resmi Tatil;{(summary.IsCompanyHoliday ? "Evet" : "Hayir")}");
        sb.AppendLine($"Rapor Olusturma;{createdAt:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"Toplam Aktif Calisan;{summary.TotalActive}");
        sb.AppendLine($"Muaf (izin/tatil);{summary.ExcusedCount}");
        sb.AppendLine($"Gelen;{summary.PresentCount}");
        sb.AppendLine($"Gelmeyen;{summary.AbsentCount}");
        sb.AppendLine($"Katilim Orani;%{rate.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("Sira;Tarih;Gun;Departman;Kullanici Adi;Ad Soyad;E-posta;Durum;Aciklama");

        var index = 1;
        foreach (var a in summary.Absent)
        {
            sb.AppendLine(string.Join(';',
                index++,
                summary.Date.ToString("yyyy-MM-dd"),
                Csv(DayName(summary.Date)),
                Csv(a.Department),
                Csv(a.UserName),
                Csv(a.FullName),
                Csv(a.Email ?? ""),
                "Gelmedi",
                "Giris kaydi yok"));
        }

        if (summary.Absent.Count == 0)
            sb.AppendLine(";—;—;—;—;—;—;Gelmedi kaydi yok;—");

        return sb.ToString();
    }

    private async Task<TodaySummaryResponse> BuildSummaryForDateAsync(DateOnly date, int locationId, string? department = null)
    {
        var activeEmployees = await GetActiveEmployeesAsync(locationId, department);
        var excusedIds = await GetExcusedUserIdsAsync(locationId, date);
        var isCompanyHoliday = await _db.NonWorkingDays.AnyAsync(n =>
            n.LocationId == locationId && n.Date == date && n.UserId == null && n.Type == NonWorkingDayTypes.Holiday);

        var presentRecords = await _db.AttendanceRecords
            .Include(a => a.User)
            .Where(a => a.LocationId == locationId && a.LocalDate == date)
            .OrderBy(a => a.CheckInUtc)
            .ToListAsync();

        if (!string.IsNullOrWhiteSpace(department))
            presentRecords = presentRecords.Where(r => r.User?.Department == department).ToList();

        var presentUserIds = presentRecords.Select(r => r.UserId).ToHashSet();
        var resolvedPolicy = await _policy.GetResolvedAsync(locationId);
        var present = presentRecords
            .Select(r => AttendanceController.MapToReportItem(
                r,
                r.User ?? new ApplicationUser
                {
                    Id = r.UserId,
                    UserName = r.UserId,
                    FullName = "(bilinmeyen kullanıcı)",
                    Department = Departments.Teknik
                },
                resolvedPolicy.IsLate(r.CheckInUtc)))
            .ToList();

        var excused = activeEmployees.Where(e => excusedIds.Contains(e.UserId)).ToList();
        var absent = activeEmployees
            .Where(e => !presentUserIds.Contains(e.UserId) && !excusedIds.Contains(e.UserId))
            .ToList();

        return new TodaySummaryResponse
        {
            Date = date,
            TotalActive = activeEmployees.Count,
            ExcusedCount = excused.Count,
            PresentCount = activeEmployees.Count(e => presentUserIds.Contains(e.UserId)),
            AbsentCount = absent.Count,
            IsCompanyHoliday = isCompanyHoliday,
            Present = present,
            Absent = absent,
            Excused = excused
        };
    }

    private async Task<HashSet<string>> GetExcusedUserIdsAsync(int locationId, DateOnly date)
    {
        var days = await _db.NonWorkingDays.Where(n => n.LocationId == locationId && n.Date == date).ToListAsync();
        var result = new HashSet<string>();

        if (days.Any(d => d.UserId is null && d.Type == NonWorkingDayTypes.Holiday))
        {
            var allIds = await GetActiveEmployeeIdsAsync(locationId);
            foreach (var id in allIds) result.Add(id);
            return result;
        }

        foreach (var d in days.Where(d => d.UserId is not null))
            result.Add(d.UserId!);

        return result;
    }

    private async Task<List<string>> GetActiveEmployeeIdsAsync(int locationId)
    {
        var employeeRoleId = await _db.Roles
            .Where(r => r.Name == Roles.Employee)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        return await _db.Users
            .Where(u => u.IsActive && u.LocationId == locationId &&
                        _db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == employeeRoleId))
            .Select(u => u.Id)
            .ToListAsync();
    }

    private async Task<List<TodaySummaryEmployee>> GetActiveEmployeesAsync(int locationId, string? department = null)
    {
        var employeeRoleId = await _db.Roles
            .Where(r => r.Name == Roles.Employee)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        var query = _db.Users
            .Where(u => u.IsActive && u.LocationId == locationId &&
                        _db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == employeeRoleId));

        if (!string.IsNullOrWhiteSpace(department))
            query = query.Where(u => u.Department == department);

        return await query
            .OrderBy(u => u.FullName)
            .Select(u => new TodaySummaryEmployee
            {
                UserId = u.Id,
                UserName = u.UserName!,
                FullName = u.FullName,
                Email = u.Email,
                Department = u.Department
            })
            .ToListAsync();
    }

    private static string DayName(DateOnly date)
        => date.ToDateTime(TimeOnly.MinValue).ToString("dddd", TrCulture);

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private async Task<List<AttendanceReportItem>> QueryAsync(
        int locationId, DateOnly? from, DateOnly? to, string? userId, string? department)
    {
        var query = _db.AttendanceRecords
            .Include(a => a.User)
            .Where(a => a.LocationId == locationId)
            .AsQueryable();
        if (from is not null) query = query.Where(a => a.LocalDate >= from);
        if (to is not null) query = query.Where(a => a.LocalDate <= to);
        if (!string.IsNullOrWhiteSpace(userId)) query = query.Where(a => a.UserId == userId);
        if (!string.IsNullOrWhiteSpace(department)) query = query.Where(a => a.User!.Department == department);

        var records = await query.OrderByDescending(a => a.CheckInUtc).ToListAsync();
        var resolvedPolicy = await _policy.GetResolvedAsync(locationId);

        return records
            .Select(r => AttendanceController.MapToReportItem(
                r,
                r.User ?? new ApplicationUser
                {
                    Id = r.UserId,
                    UserName = r.UserId,
                    FullName = "(bilinmeyen kullanıcı)",
                    Department = Departments.Teknik
                },
                resolvedPolicy.IsLate(r.CheckInUtc)))
            .ToList();
    }
}
