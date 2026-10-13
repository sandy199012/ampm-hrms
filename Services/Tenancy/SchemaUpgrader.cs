using System.Data.Common;
using AmpmHrmsPro.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AmpmHrmsPro.Services.Tenancy
{
    /// <summary>
    /// Lightweight auto-migration used at startup (the app has no EF
    /// migrations — tables are created from the model with IF NOT EXISTS).
    /// This adds any column the model has but an existing table lacks
    /// (e.g. CompanyId on every table, new Employee security fields) and
    /// swaps the old single-company unique indexes for per-company ones.
    /// Safe to run on every boot.
    /// </summary>
    public static class SchemaUpgrader
    {
        public static void AddMissingColumns(AppDbContext db, DbConnection conn, int defaultCompanyId = 1)
        {
            bool pg = db.Database.IsNpgsql();
            var existing = ReadColumns(conn, pg);
            int added = 0;

            foreach (var table in db.Model.GetRelationalModel().Tables)
            {
                if (!existing.TryGetValue(table.Name, out var cols)) continue;   // new table → created by the CREATE script
                foreach (var col in table.Columns)
                {
                    if (cols.Contains(col.Name)) continue;
                    var def = col.IsNullable ? "NULL" : "NOT NULL DEFAULT " + DefaultFor(col, pg, defaultCompanyId);
                    var sql = pg
                        ? $"ALTER TABLE \"{table.Name}\" ADD COLUMN IF NOT EXISTS \"{col.Name}\" {col.StoreType} {def}"
                        : $"ALTER TABLE \"{table.Name}\" ADD COLUMN \"{col.Name}\" {col.StoreType} {def}";
                    if (Exec(conn, sql)) added++;
                    // A forgotten CompanyId must fail loudly, never fall back to company 1.
                    if (pg && col.Name == AppDbContext.TenantColumn)
                        Exec(conn, $"ALTER TABLE \"{table.Name}\" ALTER COLUMN \"{col.Name}\" DROP DEFAULT");
                }
            }
            if (added > 0) Console.WriteLine($"✅ Schema upgrade: {added} column(s) added");
        }

        /// <summary>Old "unique per database" indexes (e.g. IX_Employees_EmpCode) → dropped; the per-company ones replace them.</summary>
        public static void DropSingleCompanyUniqueIndexes(AppDbContext db, DbConnection conn)
        {
            foreach (var table in db.Model.GetRelationalModel().Tables)
                foreach (var ix in table.Indexes.Where(i => i.IsUnique))
                {
                    var cols = ix.Columns.Select(c => c.Name).ToList();
                    if (cols.Count < 2 || cols[0] != AppDbContext.TenantColumn) continue;
                    var legacy = $"IX_{table.Name}_{string.Join("_", cols.Skip(1))}";
                    Exec(conn, $"DROP INDEX IF EXISTS \"{legacy}\"");
                }
        }

        private static string DefaultFor(IColumn col, bool pg, int companyId)
        {
            if (col.Name == AppDbContext.TenantColumn) return companyId.ToString();
            var t = Nullable.GetUnderlyingType(col.ProviderClrType) ?? col.ProviderClrType;
            if (t == typeof(bool)) return pg ? "false" : "0";
            if (t == typeof(string)) return "''";
            if (t == typeof(DateTime) || t == typeof(DateOnly) || t == typeof(DateTimeOffset)) return "'1900-01-01'";
            if (t == typeof(TimeOnly) || t == typeof(TimeSpan)) return "'00:00:00'";
            if (t == typeof(Guid)) return "'00000000-0000-0000-0000-000000000000'";
            return "0";
        }

        private static Dictionary<string, HashSet<string>> ReadColumns(DbConnection conn, bool pg)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (pg)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = current_schema()";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var t = r.GetString(0);
                    if (!map.TryGetValue(t, out var set)) map[t] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(r.GetString(1));
                }
            }
            else
            {
                var tables = new List<string>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) tables.Add(r.GetString(0));
                }
                foreach (var t in tables)
                {
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"PRAGMA table_info(\"{t}\")";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) set.Add(r.GetString(1));
                    map[t] = set;
                }
            }
            return map;
        }

        public static bool Exec(DbConnection conn, string sql)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️ {sql.Split('\n')[0][..Math.Min(90, sql.Length)]} → {ex.Message.Split('\n')[0]}");
                return false;
            }
        }
    }
}
