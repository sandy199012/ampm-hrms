using System.Diagnostics;
using System.IO.Compression;
using AmpmHrmsPro.Data;
using AmpmHrmsPro.Services;
using AmpmHrmsPro.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AmpmHrmsPro.Controllers
{
    // ═══════════════════════════════════════════════════════════════════
    // DATABASE BACKUP — Administration > Database Backup. Runs pg_dump
    // (installed in the Docker image) against the app's own PostgreSQL and
    // hands the admin a file to keep or to restore into another Supabase /
    // PostgreSQL project. Only for "Manage users, roles & security", and only
    // while the database holds a single company (a dump has every table).
    // ═══════════════════════════════════════════════════════════════════
    [Authorize]
    [RequirePermission(Perm.Users, Perm.Users)]
    public class BackupController : Controller
    {
        readonly AppDbContext _db;
        readonly ILogger<BackupController> _log;
        public BackupController(AppDbContext db, ILogger<BackupController> log) { _db = db; _log = log; }

        public async Task<IActionResult> Index()
        {
            ViewBag.IsPostgres = _db.Database.IsNpgsql();
            ViewBag.Companies = await _db.Companies.IgnoreQueryFilters().CountAsync();
            if (_db.Database.IsNpgsql())
            {
                var conn = _db.Database.GetDbConnection();
                await conn.OpenAsync();
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "select pg_size_pretty(pg_database_size(current_database())), split_part(current_setting('server_version'),' ',1)";
                    await using var r = await cmd.ExecuteReaderAsync();
                    if (await r.ReadAsync()) { ViewBag.DbSize = r.GetString(0); ViewBag.ServerVersion = r.GetString(1); }
                }
                finally { await conn.CloseAsync(); }
            }
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Download(string format = "sql")
        {
            if (!_db.Database.IsNpgsql()) { TempData["Error"] = "Backup works only on the PostgreSQL (Supabase) database."; return RedirectToAction("Index"); }
            if (await _db.Companies.IgnoreQueryFilters().CountAsync() > 1)
            { TempData["Error"] = "This database holds more than one company — a full backup is only allowed on a single-company database."; return RedirectToAction("Index"); }

            var cs = new NpgsqlConnectionStringBuilder(_db.Database.GetConnectionString());
            // Supabase pooler: 6543 is transaction mode (pg_dump can't use it) → 5432 = session mode.
            var port = cs.Port == 6543 ? 5432 : cs.Port;
            bool custom = format == "custom";
            var stamp = IndiaTime.Now.ToString("yyyyMMdd-HHmm");
            var tmp = Path.Combine(Path.GetTempPath(), $"hrms-backup-{Guid.NewGuid():N}.{(custom ? "dump" : "sql")}");

            var psi = new ProcessStartInfo("pg_dump")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-h", cs.Host!, "-p", port.ToString(), "-U", cs.Username!, "-d", cs.Database ?? "postgres",
                                      "--schema=public", "--no-owner", "--no-privileges", "--format=" + (custom ? "custom" : "plain"), "-f", tmp })
                psi.ArgumentList.Add(a);
            if (!custom) psi.ArgumentList.Add("--inserts");          // plain INSERTs restore anywhere (psql / SQL editor)
            psi.Environment["PGPASSWORD"] = cs.Password ?? "";
            psi.Environment["PGSSLMODE"] = "require";
            psi.Environment["PGCONNECT_TIMEOUT"] = "20";

            string err;
            int exit;
            try
            {
                using var p = Process.Start(psi)!;
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(TimeSpan.FromMinutes(10))) { try { p.Kill(true); } catch { } throw new TimeoutException("pg_dump took longer than 10 minutes."); }
                err = await errTask; exit = p.ExitCode;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
            {
                TryDelete(tmp);
                _log.LogError(ex, "Backup failed");
                TempData["Error"] = ex is TimeoutException ? ex.Message : "pg_dump is not installed on this server.";
                return RedirectToAction("Index");
            }
            if (exit != 0 || !System.IO.File.Exists(tmp))
            {
                TryDelete(tmp);
                var msg = (err ?? "").Replace(cs.Password ?? "\u0000", "***");
                _log.LogError("Backup failed: {Err}", msg);
                TempData["Error"] = "Backup failed: " + (msg.Length > 400 ? msg[..400] : msg);
                return RedirectToAction("Index");
            }

            _db.Audit("DatabaseBackup", $"Full database backup downloaded ({(custom ? "pg_restore .dump" : "SQL .sql.gz")}) by {User.Identity?.Name}");
            await _db.SaveChangesAsync();

            if (custom)
                return PhysicalFileAndDelete(tmp, "application/octet-stream", $"ampm-hrms-backup-{stamp}.dump");

            // gzip the plain SQL (~10× smaller to download)
            var gz = tmp + ".gz";
            await using (var src = System.IO.File.OpenRead(tmp))
            await using (var dst = System.IO.File.Create(gz))
            await using (var zip = new GZipStream(dst, CompressionLevel.Optimal))
                await src.CopyToAsync(zip);
            TryDelete(tmp);
            return PhysicalFileAndDelete(gz, "application/gzip", $"ampm-hrms-backup-{stamp}.sql.gz");
        }

        IActionResult PhysicalFileAndDelete(string path, string type, string name)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.DeleteOnClose);
            return File(stream, type, name);
        }

        static void TryDelete(string path) { try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { } }
    }
}
