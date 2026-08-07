using System.Security.Claims;
using Ledajans.Server.Data;
using Ledajans.Server.Services;
using Ledajans.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledajans.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AttendanceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAttendancePolicyService _policy;
    private readonly ILocationScope _locationScope;

    public AttendanceController(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        IAttendancePolicyService policy,
        ILocationScope locationScope)
    {
        _db = db;
        _userManager = userManager;
        _policy = policy;
        _locationScope = locationScope;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet("today")]
    public async Task<ActionResult<TodayStatusResponse>> Today()
    {
        if (!await IsActiveEmployeeAsync())
            return Unauthorized(new { message = "Hesabınız pasif." });

        var locationId = await _locationScope.GetEmployeeLocationIdAsync(UserId);
        if (locationId is null)
            return BadRequest(new { message = "Hesabınıza kurum atanmamış. Yöneticinize başvurun." });

        var today = AppTime.Today;
        var record = await FindTodayRecordAsync(UserId, today);
        var policy = await _policy.GetAsync(locationId.Value);

        return Ok(new TodayStatusResponse
        {
            HasCheckedIn = record is not null,
            CheckInUtc = record?.CheckInUtc,
            HasCheckedOut = record?.CheckOutUtc is not null,
            CheckOutUtc = record?.CheckOutUtc,
            IsLate = record is not null && await _policy.IsLateAsync(locationId.Value, record.CheckInUtc),
            WorkStartTime = policy.WorkStartTime
        });
    }

    [HttpPost("checkin")]
    public async Task<ActionResult<CheckInResponse>> CheckIn(CheckInRequest request)
    {
        if (!await IsActiveEmployeeAsync())
            return Unauthorized(new { message = "Hesabınız pasif." });

        var locationId = await _locationScope.RequireEmployeeLocationIdAsync(UserId);

        var geo = await ValidateGeofenceAsync(request, locationId);
        if (geo.Error is not null)
            return Ok(new CheckInResponse { Success = false, Message = geo.Error, DistanceMeters = geo.Distance });

        var today = AppTime.Today;
        var existing = await FindTodayRecordAsync(UserId, today);
        if (existing is not null)
            return Ok(await AlreadyCheckedInResponseAsync(existing.CheckInUtc, geo.Distance, locationId));

        var now = DateTime.UtcNow;
        _db.AttendanceRecords.Add(new AttendanceRecord
        {
            LocationId = locationId,
            UserId = UserId,
            CheckInUtc = now,
            LocalDate = today,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            DistanceMeters = geo.Distance,
            IpAddress = ClientIpHelper.GetClientIp(HttpContext)
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicateAttendance(ex))
        {
            var saved = await FindTodayRecordAsync(UserId, today);
            return Ok(saved is not null
                ? await AlreadyCheckedInResponseAsync(saved.CheckInUtc, geo.Distance, locationId)
                : new CheckInResponse { Success = false, Message = "Kayıt çakışması oluştu. Tekrar deneyin.", DistanceMeters = geo.Distance });
        }
        catch (DbUpdateException)
        {
            return Ok(new CheckInResponse
            {
                Success = false,
                Message = "Kayıt kaydedilemedi. Yöneticinize başvurun.",
                DistanceMeters = geo.Distance
            });
        }

        return Ok(new CheckInResponse
        {
            Success = true,
            Message = "Geldiğiniz başarıyla kaydedildi.",
            CheckInUtc = now,
            DistanceMeters = geo.Distance,
            IsLate = await _policy.IsLateAsync(locationId, now)
        });
    }

    [HttpPost("checkout")]
    public async Task<ActionResult<CheckOutResponse>> CheckOut(CheckInRequest request)
    {
        if (!await IsActiveEmployeeAsync())
            return Unauthorized(new { message = "Hesabınız pasif." });

        var locationId = await _locationScope.RequireEmployeeLocationIdAsync(UserId);
        var geo = await ValidateGeofenceAsync(request, locationId);
        if (geo.Error is not null)
            return Ok(new CheckOutResponse
            {
                Success = false,
                Message = geo.Error,
                DistanceMeters = geo.Distance
            });

        var today = AppTime.Today;
        var record = await FindTodayRecordEntityAsync(UserId, today);

        if (record is null)
        {
            return Ok(new CheckOutResponse
            {
                Success = false,
                Message = "Önce 'Geldim' işaretlemeniz gerekiyor.",
                DistanceMeters = geo.Distance
            });
        }

        if (record.CheckOutUtc is not null)
        {
            return Ok(new CheckOutResponse
            {
                Success = false,
                Message = "Bugün zaten 'Çıkış Yaptım' işaretlediniz.",
                DistanceMeters = geo.Distance
            });
        }

        var now = DateTime.UtcNow;
        record.CheckOutUtc = now;
        record.CheckOutLatitude = request.Latitude;
        record.CheckOutLongitude = request.Longitude;
        record.CheckOutDistanceMeters = geo.Distance;
        record.CheckOutIpAddress = ClientIpHelper.GetClientIp(HttpContext);

        await _db.SaveChangesAsync();

        return Ok(new CheckOutResponse
        {
            Success = true,
            Message = "Çıkışınız başarıyla kaydedildi.",
            CheckOutUtc = now,
            DistanceMeters = geo.Distance
        });
    }

    [HttpPost("manual")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<AttendanceReportItem>> ManualCheckIn(ManualCheckInRequest request)
    {
        var adminLocationId = await _locationScope.GetAdminLocationIdAsync();
        if (adminLocationId is null)
            return BadRequest(new { message = "Kurum seçimi gerekli." });

        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user is null)
            return NotFound(new { message = "Kullanıcı bulunamadı." });

        if (!await _userManager.IsInRoleAsync(user, Roles.Employee))
            return BadRequest(new { message = "Yalnızca çalışan hesaplarına manuel kayıt eklenebilir." });

        if (!user.IsActive)
            return BadRequest(new { message = "Pasif çalışana manuel kayıt eklenemez." });

        if (user.LocationId != adminLocationId)
            return BadRequest(new { message = "Seçili kuruma ait olmayan çalışana kayıt eklenemez." });

        var localDate = request.LocalDate ?? AppTime.Today;
        var exists = await _db.AttendanceRecords
            .AnyAsync(a => a.UserId == request.UserId && a.LocalDate == localDate);

        if (exists)
            return Conflict(new { message = "Bu tarih için zaten kayıt var." });

        var geofence = await _db.Geofences.FirstOrDefaultAsync(g => g.LocationId == adminLocationId);
        var policy = await _policy.GetResolvedAsync(adminLocationId.Value);
        var checkInUtc = localDate == AppTime.Today
            ? DateTime.UtcNow
            : AppTime.TurkeyLocalToUtc(localDate, policy.LateThreshold);
        var record = new AttendanceRecord
        {
            LocationId = adminLocationId.Value,
            UserId = request.UserId,
            CheckInUtc = checkInUtc,
            LocalDate = localDate,
            Latitude = geofence?.Latitude ?? 0,
            Longitude = geofence?.Longitude ?? 0,
            DistanceMeters = 0,
            IpAddress = "manual",
            IsManual = true,
            AdminNote = request.Note
        };

        _db.AttendanceRecords.Add(record);
        await _db.SaveChangesAsync();

        return Ok(await MapToReportItemAsync(record, user, adminLocationId.Value));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(int id)
    {
        var locationId = await _locationScope.GetAdminLocationIdAsync();
        if (locationId is null)
            return BadRequest(new { message = "Kurum seçimi gerekli." });

        var record = await _db.AttendanceRecords.FindAsync(id);
        if (record is null || record.LocationId != locationId)
            return NotFound();

        _db.AttendanceRecords.Remove(record);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("history")]
    public async Task<ActionResult<List<MyAttendanceHistoryItem>>> MyHistory([FromQuery] int limit = 100)
    {
        if (!await IsActiveEmployeeAsync())
            return Unauthorized(new { message = "Hesabınız pasif." });

        var locationId = await _locationScope.GetEmployeeLocationIdAsync(UserId);
        if (locationId is null)
            return BadRequest(new { message = "Hesabınıza kurum atanmamış. Yöneticinize başvurun." });

        if (limit is < 1 or > 500) limit = 100;

        var policy = await _policy.GetResolvedAsync(locationId.Value);
        var rows = await _db.AttendanceRecords
            .Where(a => a.UserId == UserId)
            .OrderByDescending(a => a.CheckInUtc)
            .Take(limit)
            .ToListAsync();

        var items = rows.Select(a => new MyAttendanceHistoryItem
        {
            LocalDate = a.LocalDate,
            CheckInUtc = a.CheckInUtc,
            CheckOutUtc = a.CheckOutUtc,
            DistanceMeters = a.DistanceMeters,
            IsLate = policy.IsLate(a.CheckInUtc)
        }).ToList();

        return Ok(items);
    }

    private async Task<(string? Error, double Distance)> ValidateGeofenceAsync(CheckInRequest request, int locationId)
    {
        var measured = await MeasureGeofenceAsync(request, locationId);
        if (measured.Error is not null)
            return measured;

        return (null, measured.Distance);
    }

    private async Task<(string? Error, double Distance)> MeasureGeofenceAsync(CheckInRequest request, int locationId)
    {
        if (!GeoHelper.AreValidCoordinates(request.Latitude, request.Longitude))
            return ("Geçersiz konum koordinatları.", 0);

        var geofence = await _db.Geofences.FirstOrDefaultAsync(g => g.IsActive && g.LocationId == locationId);
        if (geofence is null)
            return ("Aktif konum tanımlı değil. Yöneticinize başvurun.", 0);

        var distance = Math.Round(GeoHelper.DistanceMeters(
            geofence.Latitude, geofence.Longitude,
            request.Latitude, request.Longitude), 1);

        var accuracy = request.Accuracy is > 0 ? request.Accuracy.Value : 0;
        var maxAccuracy = await _policy.GetMaxGpsAccuracyMetersAsync();

        if (GeoHelper.IsWithinGeofence(distance, accuracy, geofence.RadiusMeters))
            return (null, distance);

        var worstCase = distance + accuracy;
        var outsideBy = Math.Max(0, Math.Round(worstCase - geofence.RadiusMeters));

        if (accuracy > maxAccuracy)
        {
            return ($"Konum doğrulanamadı (hassasiyet ±{Math.Round(accuracy)} m). Bilgisayarda pencere kenarında bekleyin; Wi-Fi ve konum izni açık olsun.", distance);
        }

        return ($"Konum sınırının dışında görünüyorsunuz (en iyi ihtimalle sınıra ~{outsideBy} m). Açık alana çıkıp tekrar deneyin.", distance);
    }

    private async Task<bool> IsActiveEmployeeAsync()
    {
        var user = await _userManager.FindByIdAsync(UserId);
        return user is not null && user.IsActive;
    }

    internal static AttendanceReportItem MapToReportItem(AttendanceRecord record, ApplicationUser user, bool isLate)
        => new()
        {
            Id = record.Id,
            UserId = record.UserId,
            UserName = user.UserName!,
            FullName = user.FullName,
            Department = user.Department,
            CheckInUtc = record.CheckInUtc,
            CheckOutUtc = record.CheckOutUtc,
            LocalDate = record.LocalDate,
            Latitude = record.Latitude,
            Longitude = record.Longitude,
            DistanceMeters = record.DistanceMeters,
            IpAddress = record.IpAddress,
            IsManual = record.IsManual,
            AdminNote = record.AdminNote,
            IsLate = isLate
        };

    private async Task<AttendanceReportItem> MapToReportItemAsync(AttendanceRecord record, ApplicationUser user, int locationId)
        => MapToReportItem(record, user, await _policy.IsLateAsync(locationId, record.CheckInUtc));

    private async Task<TodayRecordSnapshot?> FindTodayRecordAsync(string userId, DateOnly today)
        => await _db.AttendanceRecords
            .Where(a => a.UserId == userId && a.LocalDate == today)
            .Select(a => new TodayRecordSnapshot(a.CheckInUtc, a.CheckOutUtc))
            .FirstOrDefaultAsync();

    private Task<AttendanceRecord?> FindTodayRecordEntityAsync(string userId, DateOnly today)
        => _db.AttendanceRecords.FirstOrDefaultAsync(a => a.UserId == userId && a.LocalDate == today);

    private async Task<CheckInResponse> AlreadyCheckedInResponseAsync(DateTime checkInUtc, double distance, int locationId)
        => new()
        {
            Success = true,
            AlreadyCheckedIn = true,
            Message = "Bugün zaten 'Geldim' işaretlediniz.",
            CheckInUtc = checkInUtc,
            DistanceMeters = distance,
            IsLate = await _policy.IsLateAsync(locationId, checkInUtc)
        };

    private static bool IsDuplicateAttendance(DbUpdateException ex)
    {
        var text = ex.InnerException?.Message ?? ex.Message;
        return text.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("IX_AttendanceRecords_UserId_LocalDate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("1062", StringComparison.Ordinal);
    }

    private sealed record TodayRecordSnapshot(DateTime CheckInUtc, DateTime? CheckOutUtc);
}
