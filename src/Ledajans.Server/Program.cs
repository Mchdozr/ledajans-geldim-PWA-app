using System.Net;
using System.Text;
using Ledajans.Server.Data;
using Ledajans.Server.Services;
using Ledajans.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    var conn = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(conn))
        throw new InvalidOperationException("Production icin ConnectionStrings__DefaultConnection ayarlayin.");

    var jwtKey = builder.Configuration["Jwt:Key"];
    if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Contains("Change-In-Production", StringComparison.Ordinal))
        throw new InvalidOperationException("Production icin Jwt__Key ortam degiskenini ayarlayin.");
}

var runningInContainer = string.Equals(
    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

if (builder.Environment.IsDevelopment() && !runningInContainer)
{
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.ListenAnyIP(5132);
        serverOptions.ListenAnyIP(7259, listenOptions => listenOptions.UseHttps());
    });
}

var connectionString = MySqlResilience.Apply(builder.Configuration.GetConnectionString("DefaultConnection") ?? "");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(
        connectionString,
        new MySqlServerVersion(new Version(10, 11, 8)),
        mysql => mysql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null)));

builder.Services.AddHostedService<DatabaseKeepAliveService>();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequiredLength = 1;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = false;
        options.User.AllowedUserNameCharacters =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+ " +
            "çğıöşüÇĞİÖŞÜ";
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<ILookupNormalizer, TurkishLookupNormalizer>();

var jwtSettings = builder.Configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<AttendanceSettings>(builder.Configuration.GetSection(AttendanceSettings.SectionName));
builder.Services.AddScoped<IAttendancePolicyService, AttendancePolicyService>();
builder.Services.AddScoped<IDeviceBindingService, DeviceBindingService>();
builder.Services.AddScoped<ILocationScope, LocationScope>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TokenService>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("LedajansApp", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
        {
            if (builder.Environment.IsDevelopment()) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            return uri.Host.Equals("geldim.ledajans.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler(handler =>
    {
        handler.Run(async context =>
        {
            try
            {
                var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                if (ex is not null)
                {
                    if (MySqlResilience.IsPoolPoisoning(ex))
                        MySqlResilience.ClearPools();

                    ServerFaultLog.Write(
                        app.Environment.ContentRootPath,
                        "unhandled.txt",
                        $"{context.Request.Method} {context.Request.Path}{Environment.NewLine}{ex}");
                }

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = "Sunucu geçici olarak yanıt veremedi. Tekrar deneyin."
                    });
                }
            }
            catch
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        });
    });
    app.UseHsts();
}

app.UseForwardedHeaders();
if (!runningInContainer)
    app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();

var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypeProvider });

app.UseRouting();
app.UseCors("LedajansApp");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

Exception? startupError = null;

app.MapGet("/health", async (AppDbContext db) =>
{
    var pending = new List<string>();
    var deviceCount = 0;

    try
    {
        pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        deviceCount = await db.UserDevices.CountAsync();
    }
    catch (Exception ex)
    {
        if (MySqlResilience.IsPoolPoisoning(ex))
            MySqlResilience.ClearPools();

        return Results.Ok(new
        {
            status = "degraded",
            app = "Ledajans Geldim",
            startupError = startupError?.Message ?? ex.Message,
            deviceBindingEnabled = true,
            userDeviceCount = deviceCount,
            migrationsPending = pending.Count,
            migrations = pending
        });
    }

    return Results.Ok(new
    {
        status = startupError is null && pending.Count == 0 ? "ok" : "degraded",
        app = "Ledajans Geldim",
        startupError = startupError?.Message,
        deviceBindingEnabled = true,
        userDeviceCount = deviceCount,
        migrationsPending = pending.Count,
        migrations = pending
    });
});

app.MapFallbackToFile("index.html");

try
{
    await DbSeeder.SeedAsync(app.Services, app.Configuration);
}
catch (Exception ex)
{
    startupError = ex;
    var logDir = Path.Combine(app.Environment.ContentRootPath, "logs");
    Directory.CreateDirectory(logDir);
    await File.WriteAllTextAsync(
        Path.Combine(logDir, "startup-error.txt"),
        $"[{DateTime.UtcNow:O}]\n{ex}");
    Console.Error.WriteLine(ex);
}

app.Run();
