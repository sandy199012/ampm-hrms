using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
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
using AmpmHrmsPro.Services.Tenancy;
using Microsoft.AspNetCore.Authentication;
using BCrypt.Net;

// Npgsql 8.x strict timestamp mode rejects DateTime.Now (Kind=Local).
// This switch restores legacy permissive behavior so Employee.CreatedAt = DateTime.Now works.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Render injects PORT env var — use it so the load balancer can reach us
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(renderPort))
    builder.WebHost.UseUrls($"http://+:{renderPort}");

// ── Services ──
// AccessFilter enforces [RequirePermission]/[RequireModule] (custom roles +
// the company's subscription plan) on every MVC action.
builder.Services.AddControllersWithViews(o => o.Filters.Add<AccessFilter>());
builder.Services.AddMemoryCache();

// ── Multi-company (SaaS) ──
// ITenantContext = "which company is this request for" (from the login
// cookie / JWT, or set explicitly by background jobs). AppDbContext filters
// every query by it. See Services/Tenancy/*.
PlatformBrand.ProductName = builder.Configuration["Platform:ProductName"] ?? "HRMS Pro";
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<IAccessService, AccessService>();
builder.Services.AddSingleton<ICompanyInfoService, CompanyInfoService>();
builder.Services.AddScoped<ICompanyProvisioning, CompanyProvisioning>();

// ── DataProtection — persist encryption keys to Postgres so they survive
// container restarts on Render free tier. Without this, every restart
// invalidates all browser cookies and anti-forgery tokens, forcing
// every user to log in again and throwing AntiforgeryValidationException. ──
builder.Services.AddDataProtection()
    .SetApplicationName("AmpmHrmsPro")
    .PersistKeysToDbContext<AmpmHrmsPro.Data.AppDbContext>();

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
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events.OnValidatePrincipal = async ctx =>
        {
            var p = ctx.Principal;
            if (p == null) return;
            int timeout = 60;
            if (!p.IsPlatformAdmin())
            {
                var cid = p.GetCompanyId();
                if (cid == 0)   // cookie from before multi-company support
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }
                var info = await ctx.HttpContext.RequestServices.GetRequiredService<ICompanyInfoService>().GetAsync(cid);
                timeout = info?.SessionTimeoutMinutes ?? 60;
            }
            if (timeout <= 0) return;
            // Idle timeout: "la" = last activity, refreshed at most once a minute.
            DateTime? last = ctx.Properties.Items.TryGetValue("la", out var la) && long.TryParse(la, out var ticks)
                ? new DateTime(ticks, DateTimeKind.Utc) : ctx.Properties.IssuedUtc?.UtcDateTime;
            var now = DateTime.UtcNow;
            if (last.HasValue && now - last.Value > TimeSpan.FromMinutes(timeout))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            if (!last.HasValue || now - last.Value > TimeSpan.FromMinutes(1))
            {
                ctx.Properties.Items["la"] = now.Ticks.ToString();
                ctx.ShouldRenew = true;
            }
        };
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
        // Mobile tokens must belong to an active company (tokens issued
        // before multi-company support have no CompanyId → log in again).
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var cid = ctx.Principal?.GetCompanyId() ?? 0;
                if (cid == 0) { ctx.Fail("Please log in again."); return; }
                var info = await ctx.HttpContext.RequestServices.GetRequiredService<ICompanyInfoService>().GetAsync(cid);
                if (info == null || !info.CanUse) ctx.Fail(info?.IsSuspended == true ? "Company account suspended." : "Subscription expired.");
            }
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
// Email: SmartEmailSender sends via the connected Outlook or Gmail account
// ("Connect Outlook" / "Connect Gmail" on the Email Notifications page),
// falling back to SMTP when no account is connected.
builder.Services.Configure<MicrosoftGraphOptions>(builder.Configuration.GetSection("MicrosoftGraph"));
builder.Services.Configure<GoogleMailOptions>(builder.Configuration.GetSection("GoogleMail"));
builder.Services.AddScoped<IConnectedMailService, ConnectedMailService>();
builder.Services.AddScoped<SmtpEmailSender>();
builder.Services.AddScoped<IEmailSender, SmartEmailSender>();

// Permanent employee delete (Admin > All Employees > trash icon).
builder.Services.AddScoped<IEmployeeDeletionService, EmployeeDeletionService>();
builder.Services.AddScoped<IHrEmailNotificationService, HrEmailNotificationService>();
builder.Services.AddHostedService<HrNotificationHostedService>();

// ── Salary Structure / TDS / Income Tax — computes an employee's monthly
// salary breakdown from their assigned structure and their Old-vs-New
// regime tax liability from their approved investment declaration. See
// Services/PayrollTaxEngine.cs. No background job here — computed live
// on each page view (SalaryController / TaxController / MyTaxController). ──
builder.Services.AddScoped<IPayrollTaxEngine, PayrollTaxEngine>();

// ── Attendance Review (Presence 360 gap analysis) — parses the daily CSV,
// detects 4 gap types vs the roster, and emails pilot managers each morning.
// Pilot manager list and email schedule are in appsettings.json under
// "AttendanceReview". ──
builder.Services.Configure<AttendanceReviewOptions>(
    builder.Configuration.GetSection("AttendanceReview"));
builder.Services.AddScoped<IGapAnalysisReport, GapAnalysisReport>();
builder.Services.AddScoped<IAttendanceReviewService, AttendanceReviewService>();
builder.Services.AddHostedService<AttendanceReviewEmailHostedService>();

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
app.UseMiddleware<TenantGuardMiddleware>();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", server = PlatformBrand.ProductName, time = DateTime.Now }));

// ── DB Init + Seed ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
    int defaultCompanyId = 1;

    // ── 1. Schema ──────────────────────────────────────────────────────────
    try
    {
        var creator = db.Database.GetService<IRelationalDatabaseCreator>();
        if (!creator.Exists())
            creator.Create();

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        // a) Existing tables: add any new columns (CompanyId on every table,
        //    security fields …) BEFORE the create script builds indexes on them.
        SchemaUpgrader.AddMissingColumns(db, conn);
        // b) Unique indexes are now per company (two companies may both have
        //    an employee "E001" or a department "HR") — drop the old ones.
        SchemaUpgrader.DropSingleCompanyUniqueIndexes(db, conn);

        // c) Generate full EF Core DDL and inject IF NOT EXISTS on every
        //    CREATE statement so re-runs are always safe. Execute via raw
        //    ADO.NET (avoids EF parameter parsing misreading '@' chars).
        Console.WriteLine("⚙️  Applying schema (IF NOT EXISTS)...");
        var script = db.Database.GenerateCreateScript();
        int ok = 0, skipped = 0;
        foreach (var rawSql in script.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var sql = rawSql;
            if      (sql.StartsWith("CREATE TABLE ",        StringComparison.OrdinalIgnoreCase))
                sql = "CREATE TABLE IF NOT EXISTS "        + sql["CREATE TABLE ".Length..];
            else if (sql.StartsWith("CREATE UNIQUE INDEX ", StringComparison.OrdinalIgnoreCase))
                sql = "CREATE UNIQUE INDEX IF NOT EXISTS " + sql["CREATE UNIQUE INDEX ".Length..];
            else if (sql.StartsWith("CREATE INDEX ",        StringComparison.OrdinalIgnoreCase))
                sql = "CREATE INDEX IF NOT EXISTS "        + sql["CREATE INDEX ".Length..];
            else if (sql.StartsWith("CREATE SEQUENCE ",     StringComparison.OrdinalIgnoreCase))
                sql = "CREATE SEQUENCE IF NOT EXISTS "     + sql["CREATE SEQUENCE ".Length..];
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                ok++;
            }
            catch (Exception stmtEx)
            {
                if (!stmtEx.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"  ⚠️ Skipped: {stmtEx.Message.Split('\n')[0].Trim()}");
                skipped++;
            }
        }
        Console.WriteLine($"✅ Schema: {ok} applied, {skipped} already existed");
        SchemaUpgrader.AddMissingColumns(db, conn);

        // d) The first company. Everything already in the database (from the
        //    single-company version) belongs to it — the CompanyId columns
        //    added above default to 1, and this is the first row (Id 1).
        var first = db.Companies.OrderBy(c => c.Id).FirstOrDefault();
        if (first == null)
        {
            first = new Company
            {
                Code = CompanyProvisioning.NormalizeCode(app.Configuration["DefaultCompany:Code"] ?? "AMPM"),
                Name = app.Configuration["DefaultCompany:Name"] ?? "AMPM Fashions Pvt Ltd",
                Plan = "Enterprise",
                MaxEmployees = 0,
                Modules = Modules.AllKeys,
                Status = "Active",
                EmailSenderName = app.Configuration["DefaultCompany:Name"] ?? "AMPM Fashions Pvt Ltd",
            };
            db.Companies.Add(first);
            db.SaveChanges();
            Console.WriteLine($"✅ Company #{first.Id} '{first.Code}' created — existing data assigned to it");
            if (first.Id != 1)
            {
                foreach (var t in db.Model.GetRelationalModel().Tables.Where(t => t.Columns.Any(c => c.Name == AppDbContext.TenantColumn)))
                    SchemaUpgrader.Exec(conn, $"UPDATE \"{t.Name}\" SET \"{AppDbContext.TenantColumn}\" = {first.Id} WHERE \"{AppDbContext.TenantColumn}\" = 1");
            }
        }
        defaultCompanyId = first.Id;

        // e) Platform owner login for /Platform (create companies, plans …).
        if (!db.PlatformUsers.Any())
        {
            var ownerPwd = app.Configuration["Platform:OwnerPassword"] ?? Environment.GetEnvironmentVariable("PLATFORM_OWNER_PASSWORD");
            db.PlatformUsers.Add(new PlatformUser
            {
                Email = app.Configuration["Platform:OwnerEmail"] ?? Environment.GetEnvironmentVariable("PLATFORM_OWNER_EMAIL") ?? "owner",
                Name = "Platform Owner",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(ownerPwd ?? "Owner@12345"),
                MustChangePassword = string.IsNullOrEmpty(ownerPwd),
            });
            db.SaveChanges();
            Console.WriteLine("✅ Platform owner created — /Platform/Login (change the password on first login)");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("❌ Schema init failed:"); Console.WriteLine(ex);
        if (app.Environment.IsDevelopment()) throw;
    }

    // Everything below works inside the first company.
    tenant.Use(defaultCompanyId);
    db.AuditEnabled = false;

    // ── 2. Reference data (best-effort) ────────────────────────────────────
    try
    {
        SeedData.Run(db, app.Configuration);
        Console.WriteLine("✅ SeedData completed");
    }
    catch (Exception seedEx)
    {
        // Partial failures are expected on re-deploys when some tables
        // already have data. The admin fallback below runs regardless.
        Console.WriteLine($"⚠️ SeedData partial: {seedEx.Message.Split('\n')[0]}");
    }

    // ── 3. Admin user guarantee (first company) ────────────────────────────
    // Runs in its own block so a SeedData failure never locks out the app.
    // Idempotent: skips insert when ADMIN001 already exists.
    try
    {
        if (!db.Employees.Any(e => e.EmpCode == "ADMIN001"))
        {
            Console.WriteLine("⚠️  ADMIN001 missing — creating now...");
            db.Employees.Add(new Employee
            {
                EmpCode      = "ADMIN001",
                Name         = "HR Admin",
                Email        = app.Configuration["AdminEmail"] ?? "admin@ampmfashions.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                    app.Configuration["AdminPassword"] ?? "AMPM@Admin123"),
                Role         = "admin",
                Status       = "Active",
                IsActive     = true,
                CreatedAt    = DateTime.UtcNow
            });
            db.SaveChanges();
            Console.WriteLine("✅ Admin created — login: ADMIN001 / AMPM@Admin123");
        }
        else
        {
            Console.WriteLine("✅ Admin ADMIN001 exists");
        }
    }
    catch (Exception adminEx)
    {
        Console.WriteLine($"❌ Admin creation failed: {adminEx.Message}");
    }

    Console.WriteLine("✅ Database ready");
}

Console.WriteLine($"\n🚀 {PlatformBrand.ProductName} — multi-company HRMS");
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
Console.WriteLine("   Company login: /Account/Login   ·   Owner console: /Platform/Login\n");

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
