using System.Globalization;
using Ledajans.Server.Data;
using Ledajans.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ledajans.Server.Services;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () => await db.Database.MigrateAsync());
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                "Veritabani migration tamamlanamadi: " + string.Join(", ", pending));
        }

        foreach (var role in new[] { Roles.Admin, Roles.Employee })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        await EnsureLocationsAsync(db, config);

        var adminUser = config["Seed:AdminUserName"] ?? "admin";
        var adminPass = config["Seed:AdminPassword"] ?? "Admin123!";
        var adminFullName = config["Seed:AdminFullName"] ?? "Yönetici";

        if (await userManager.FindByNameAsync(adminUser) is null)
        {
            var admin = new ApplicationUser
            {
                UserName = adminUser,
                Email = config["Seed:AdminEmail"] ?? "admin@ledajans.local",
                EmailConfirmed = true,
                FullName = adminFullName,
                IsActive = true
            };
            var result = await userManager.CreateAsync(admin, adminPass);
            if (result.Succeeded)
                await userManager.AddToRoleAsync(admin, Roles.Admin);
        }

        if (config.GetValue("Seed:CreateDemoEmployees", false))
        {
            var ofisId = await db.Locations
                .Where(l => l.Code == LocationCodes.Ofis)
                .Select(l => l.Id)
                .FirstAsync();

            await EnsureDemoEmployeeAsync(userManager, "calisan1", "Test Calisan 1", "Calisan123!", ofisId);
            await EnsureDemoEmployeeAsync(userManager, "calisan2", "Test Calisan 2", "Calisan123!", ofisId);
        }
    }

    private static async Task EnsureLocationsAsync(AppDbContext db, IConfiguration config)
    {
        var inv = CultureInfo.InvariantCulture;

        if (!await db.Locations.AnyAsync())
        {
            db.Locations.AddRange(
                new Location { Name = LocationNames.Ofis, Code = LocationCodes.Ofis, SortOrder = 1, IsActive = true },
                new Location { Name = LocationNames.Fabrika, Code = LocationCodes.Fabrika, SortOrder = 2, IsActive = true });
            await db.SaveChangesAsync();
        }

        var ofis = await db.Locations.FirstAsync(l => l.Code == LocationCodes.Ofis);
        var fabrika = await db.Locations.FirstAsync(l => l.Code == LocationCodes.Fabrika);

        if (!await db.Geofences.AnyAsync(g => g.LocationId == ofis.Id))
        {
            var lat = double.TryParse(config["Seed:DefaultLat"], NumberStyles.Float, inv, out var parsedLat)
                ? parsedLat : OfficeDefaults.Latitude;
            var lng = double.TryParse(config["Seed:DefaultLng"], NumberStyles.Float, inv, out var parsedLng)
                ? parsedLng : OfficeDefaults.Longitude;
            var radius = int.TryParse(config["Seed:DefaultRadius"], NumberStyles.Integer, inv, out var r)
                ? r : OfficeDefaults.RadiusMeters;

            db.Geofences.Add(new Geofence
            {
                LocationId = ofis.Id,
                Name = OfficeDefaults.Name,
                Latitude = lat,
                Longitude = lng,
                RadiusMeters = radius,
                IsActive = true
            });
        }
        else
        {
            var geofence = await db.Geofences.FirstAsync(g => g.LocationId == ofis.Id);
            const double oldLat = 41.0082;
            const double oldLng = 28.9784;
            if (Math.Abs(geofence.Latitude - oldLat) < 0.0001 && Math.Abs(geofence.Longitude - oldLng) < 0.0001)
            {
                geofence.Latitude = OfficeDefaults.Latitude;
                geofence.Longitude = OfficeDefaults.Longitude;
                await db.SaveChangesAsync();
            }
        }

        if (!await db.Geofences.AnyAsync(g => g.LocationId == fabrika.Id))
        {
            var fabLat = double.TryParse(config["Seed:FabrikaLat"], NumberStyles.Float, inv, out var parsedFabLat)
                ? parsedFabLat : FactoryDefaults.Latitude;
            var fabLng = double.TryParse(config["Seed:FabrikaLng"], NumberStyles.Float, inv, out var parsedFabLng)
                ? parsedFabLng : FactoryDefaults.Longitude;
            var fabRadius = int.TryParse(config["Seed:FabrikaRadius"], NumberStyles.Integer, inv, out var fabR)
                ? fabR : FactoryDefaults.RadiusMeters;

            db.Geofences.Add(new Geofence
            {
                LocationId = fabrika.Id,
                Name = FactoryDefaults.Name,
                Latitude = fabLat,
                Longitude = fabLng,
                RadiusMeters = fabRadius,
                IsActive = true
            });
        }

        foreach (var locationId in new[] { ofis.Id, fabrika.Id })
        {
            if (!await db.CompanySettings.AnyAsync(s => s.LocationId == locationId))
            {
                db.CompanySettings.Add(new CompanySettings { LocationId = locationId });
            }

            await EnsureDefaultDepartmentsAsync(db, locationId);
        }

        await db.SaveChangesAsync();

        var employeeRoleId = await db.Roles
            .Where(r => r.Name == Roles.Employee)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        if (employeeRoleId is not null)
        {
            var employeesWithoutLocation = await db.Users
                .Where(u => u.LocationId == null &&
                            db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == employeeRoleId))
                .ToListAsync();

            foreach (var employee in employeesWithoutLocation)
                employee.LocationId = ofis.Id;

            if (employeesWithoutLocation.Count > 0)
                await db.SaveChangesAsync();
        }
    }

    private static async Task EnsureDefaultDepartmentsAsync(AppDbContext db, int locationId)
    {
        if (await db.Departments.AnyAsync(d => d.LocationId == locationId))
            return;

        for (var i = 0; i < Departments.All.Length; i++)
        {
            db.Departments.Add(new Department
            {
                LocationId = locationId,
                Name = Departments.All[i],
                SortOrder = i + 1
            });
        }
    }

    private static async Task EnsureDemoEmployeeAsync(
        UserManager<ApplicationUser> userManager,
        string userName,
        string fullName,
        string password,
        int locationId)
    {
        if (await userManager.FindByNameAsync(userName) is not null)
            return;

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@ledajans.local",
            EmailConfirmed = true,
            FullName = fullName,
            Department = Departments.Teknik,
            IsActive = true,
            LocationId = locationId
        };

        var result = await userManager.CreateAsync(user, password);
        if (result.Succeeded)
            await userManager.AddToRoleAsync(user, Roles.Employee);
    }
}
