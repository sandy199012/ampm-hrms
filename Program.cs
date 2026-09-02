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
    app.UseExceptionHandler("/Home/Error");
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

        // Step 1: Ensure the database itself exists (no-op on Supabase —
        // the 'postgres' DB always exists there).
        if (!creator.Exists())
            creator.Create();

        // Step 2: Apply schema IDEMPOTENTLY using IF NOT EXISTS.
        //
        // WHY NOT HasTables()+CreateTables():
        //   • Supabase always has an existing 'postgres' DB, so HasTables()
        //     returns true even when none of OUR tables exist yet, causing
        //     CreateTables() to be skipped entirely.
        //   • Even if called, CreateTables() uses plain CREATE TABLE (no
        //     IF NOT EXISTS), so the first already-existing object aborts
        //     the whole batch and leaves the rest uncreated.
        //
        // SOLUTION: Generate the full EF Core DDL script and inject
        // IF NOT EXISTS into every CREATE statement so each one is a safe
        // no-op when the object already exists. Execute one statement at a
        // time via raw ADO.NET (bypasses EF parameter parsing that would
        // misinterpret '@' chars in PostgreSQL DDL).
        Console.WriteLine("⚙️  Applying schema (idempotent IF NOT EXISTS)...");
        var script = db.Database.GenerateCreateScript();

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        int ok = 0, skipped = 0;
        foreach (var rawSql in script.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            // Inject IF NOT EXISTS for each DDL object type
            var sql = rawSql;
            if (sql.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase))
                sql = "CREATE TABLE IF NOT EXISTS " + sql["CREATE TABLE ".Length..];
            else if (sql.StartsWith("CREATE UNIQUE INDEX ", StringComparison.OrdinalIgnoreCase))
                sql = "CREATE UNIQUE INDEX IF NOT EXISTS " + sql["CREATE UNIQUE INDEX ".Length..];
            else if (sql.StartsWith("CREATE INDEX ", StringComparison.OrdinalIgnoreCase))
                sql = "CREATE INDEX IF NOT EXISTS " + sql["CREATE INDEX ".Length..];
            else if (sql.StartsWith("CREATE SEQUENCE ", StringComparison.OrdinalIgnoreCase))
                sql = "CREATE SEQUENCE IF NOT EXISTS " + sql["CREATE SEQUENCE ".Length..];

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                ok++;
            }
            catch (Exception stmtEx)
            {
                Console.WriteLine($"  ⚠️ Skipped (already exists): {stmtEx.Message.Split('\n')[0].Trim()}");
                skipped++;
            }
        }
        Console.WriteLine($"✅ Schema: {ok} applied, {skipped} already existed");

        SeedData.Run(db, app.Configuration);
        Console.WriteLine("✅ Database ready");
    }
    catch (Exception ex)
    {
        Console.WriteLine("❌ DB INIT FAILED:");
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
