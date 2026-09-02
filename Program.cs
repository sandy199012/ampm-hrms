using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Models;
using AmpmHrmsPro.Services;
using BCrypt.Net;

var builder = WebApplication.CreateBuilder(args);

// Render injects PORT env var — use it so the load balancer can reach us
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(renderPort))
    builder.WebHost.UseUrls($"http://+:{renderPort}");

// ── Services ──
builder.Services.AddControllersWithViews();

// DATABASE: cloud (Render) → PostgreSQL via DATABASE_URL env var
//           local dev      → SQLite (ampm_hrms.db in project folder)
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrEmpty(databaseUrl))
{
    // DATABASE_URL from Render/Supabase is a URI like:
    // postgresql://user:password@host:port/dbname
    // Use LastIndexOf('@') so passwords containing '@' are handled correctly.
    string pgConnectionString;
    try
    {
        var withoutScheme = databaseUrl.Substring(databaseUrl.IndexOf("://") + 3);
        var lastAt        = withoutScheme.LastIndexOf('@');
        var userInfo      = withoutScheme.Substring(0, lastAt);
        var hostPart      = withoutScheme.Substring(lastAt + 1);

        var colonInUser = userInfo.IndexOf(':');
        var username = Uri.UnescapeDataString(colonInUser >= 0 ? userInfo.Substring(0, colonInUser) : userInfo);
        var password = colonInUser >= 0 ? Uri.UnescapeDataString(userInfo.Substring(colonInUser + 1)) : "";

        var slashIdx = hostPart.IndexOf('/');
        var hostPort = slashIdx >= 0 ? hostPart.Substring(0, slashIdx) : hostPart;
        var database = slashIdx >= 0 ? hostPart.Substring(slashIdx + 1) : "postgres";

        var colonInHost = hostPort.LastIndexOf(':');
        var host = colonInHost >= 0 ? hostPort.Substring(0, colonInHost) : hostPort;
        var port = colonInHost >= 0 && int.TryParse(hostPort.Substring(colonInHost + 1), out var p) ? p : 5432;

        pgConnectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true";
        Console.WriteLine($"✅ PostgreSQL parsed — Host={host} DB={database} User={username}");
    }
    catch
    {
        pgConnectionString = databaseUrl;
    }
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(pgConnectionString));
}
else
{
    var dbPath = Path.Combine(
        Environment.GetEnvironmentVariable("DB_PATH") ?? ".",
        "ampm_hrms.db"
    );
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));
}

// Cookie stays the DEFAULT scheme (every existing MVC [Authorize] attribute
// with no explicit scheme keeps using it, unchanged). JwtBearer is added
// alongside it, not instead of it — only the Mobile* API controllers under
// Controllers/Api/ opt into it explicitly via
// [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)],
// since a mobile app has no browser cookie jar to carry the web login in.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath        = "/Account/Login";
        options.LogoutPath       = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan   = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name     = "AmpmHrmsPro";
        options.Cookie.HttpOnly = true;
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var jwtKey = builder.Configuration["Jwt:Key"] ?? "AMPM-HRMS-Mobile-Fallback-Key-Please-Configure-32chars+";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };
    });

builder.Services.AddHttpContextAccessor();

// ── Biometric attendance sync — generic HTTP client + the vendor-agnostic
// sync service, plus a background poller that runs it on a timer whenever
// Admin > Attendance > API Settings has it enabled (see BiometricSyncService.cs). ──
builder.Services.AddHttpClient();
builder.Services.AddScoped<IBiometricSyncService, BiometricSyncService>();
builder.Services.AddHostedService<BiometricSyncHostedService>();

// ── Mobile app support — JWT issuing/validation for the React Native app's
// login, and the vendor-agnostic face-match verification called from
// MobileAttendanceController's punch endpoint (see FaceMatchService.cs). ──
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IFaceMatchService, FaceMatchService>();

// ── HR email notifications — Late/Early + Miss Punch alerts, birthday
// wishes, and the weekly attendance report, all department-wise to each
// department's Head. Configured from Admin > Attendance > Email
// Notifications (EmailSettings) and fired by a background poller, same
// pattern as the biometric sync above (see HrEmailNotificationService.cs). ──
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<IHrEmailNotificationService, HrEmailNotificationService>();
builder.Services.AddHostedService<HrNotificationHostedService>();

// ── Salary Structure / TDS / Income Tax — computes an employee's monthly
// salary breakdown from their assigned structure and their Old-vs-New
// regime tax liability from their approved investment declaration. See
// Services/PayrollTaxEngine.cs. No background job here — computed live
// on each page view (SalaryController / TaxController / MyTaxController). ──
builder.Services.AddScoped<IPayrollTaxEngine, PayrollTaxEngine>();

var app = builder.Build();

// ── Force the local-testing port ──
// A hard-set port here can never drift from launchSettings.json, IIS
// Express config, or a stray ASPNETCORE_URLS environment variable — it's
// the single source of truth while developing locally. (Only applies in
// Development — once this is actually hosted, normal hosting config
// takes over.)
if (app.Environment.IsDevelopment())
{
    app.Urls.Clear();
    app.Urls.Add("http://0.0.0.0:9090");
}

// ── Middleware ──

// Render (and most reverse-proxy cloud hosts) terminate TLS at the load
// balancer and forward plain HTTP to the container with X-Forwarded-For /
// X-Forwarded-Proto headers. Without this middleware the app sees every
// request as plain HTTP, which breaks anti-forgery token validation:
// the browser submits the form from https://..., the Referer/Origin header
// says "https", but the app thinks the scheme is "http" → mismatch →
// [ValidateAntiForgeryToken] throws → "Something went wrong".
// This must be the FIRST middleware in the pipeline.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    // TEMPORARY DIAGNOSTIC: show full exception in browser so we can see
    // the real error without needing Render dashboard access.
    // Replace this with app.UseExceptionHandler("/Home/Error") once fixed.
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var ex = context.Features
                .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            Console.WriteLine("❌ REQUEST EXCEPTION:\n" + ex);
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                "<!DOCTYPE html><html><body style='font-family:monospace;padding:2em'>" +
                "<h2 style='color:red'>⚠️ Error Detail (diagnostic mode)</h2><pre>" +
                System.Net.WebUtility.HtmlEncode(ex?.ToString() ?? "No exception captured") +
                "</pre></body></html>");
        });
    });
    // NOTE: UseHsts and UseHttpsRedirection are intentionally omitted here.
    // Render (and most cloud hosts) terminate SSL at the load balancer and
    // forward plain HTTP to the container — the app must NOT redirect to
    // HTTPS itself or the browser gets a 400 "Bad Request" loop.
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", server = "AmpmHrmsPro", time = DateTime.Now }));

// ── DB Init + Seed ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        var creator = db.Database.GetService<IRelationalDatabaseCreator>();

        // Step 1: Create the database itself if it doesn't exist
        if (!creator.Exists())
            creator.Create();

        // Step 2: Create tables if they don't exist yet.
        // EnsureCreated() skips table creation when the DB already exists
        // (e.g. Supabase always has an existing 'postgres' DB), so we use
        // HasTables() + CreateTables() to force schema creation reliably.
        if (!creator.HasTables())
        {
            Console.WriteLine("⚠️  No tables found — creating schema now...");
            creator.CreateTables();
            Console.WriteLine("✅ Schema created");
        }
        else
        {
            Console.WriteLine("✅ Schema already present");
        }

        SeedData.Run(db, app.Configuration);
        Console.WriteLine("✅ Database ready");
    }
    catch (Exception ex)
    {
        Console.WriteLine("❌ DB INIT FAILED — this is the real error, please share this if the app still won't start:");
        Console.WriteLine(ex);
        if (app.Environment.IsDevelopment())
            throw;
    }
}

Console.WriteLine("\n🚀 AMPM Fashions HRMS Pro — LOCAL TESTING MODE");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("   On this PC:");
Console.WriteLine("     App:        http://localhost:9090");
Console.WriteLine("     API health: http://localhost:9090/api/health");
try
{
    var localIps = Dns.GetHostEntry(Dns.GetHostName())
        .AddressList
        .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip));
    foreach (var ip in localIps)
        Console.WriteLine($"   From your PHONE (same WiFi): http://{ip}:9090");
}
catch { /* best-effort only */ }
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("   Login: ADMIN001 / AMPM@Admin123\n");

if (app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var url = addresses?.FirstOrDefault(a => a.StartsWith("http://"))
                ?.Replace("://0.0.0.0", "://localhost")
                ?.Replace("://[::]", "://localhost")
                ?.Replace("://+", "://localhost");
            url ??= "http://localhost:9090";

            Process.Start(new ProcessStartInfo
            {
                FileName = url.TrimEnd('/') + "/Account/Login",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Could not auto-open the browser ({ex.Message}). Open the URL above manually.");
        }
    });
}

app.Run();
