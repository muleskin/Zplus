using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ZPlus.Server.Data;

/// <summary>
/// Brings an existing SQLite database up to the current schema without losing data.
/// <see cref="RelationalDatabaseFacadeExtensions"/>'s EnsureCreated only builds a
/// brand-new database; when the model gains a table (e.g. MeetingInvitations) older
/// databases are left without it. This creates any tables and indexes the model defines
/// that the database does not yet have, using EF Core's own generated DDL so the result
/// always matches the model. Existing objects and their data are never touched.
/// </summary>
public static class SchemaUpgrader
{
    /// <summary>Creates missing tables/indexes and returns the names of anything it created.</summary>
    public static List<string> EnsureUpToDate(AppDbContext db)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        db.Database.OpenConnection();
        try
        {
            using var query = db.Database.GetDbConnection().CreateCommand();
            query.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','index')";
            using var reader = query.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(0));
        }
        finally
        {
            db.Database.CloseConnection();
        }

        // EF's full CREATE script for the model. Run in a safe order: create missing TABLES,
        // then add missing COLUMNS to existing tables, then create missing INDEXES last — an
        // index may reference a column that was only just added, so it must come after columns.
        var created = new List<string>();
        var deferredIndexes = new List<(string Name, string Sql)>();
        foreach (var statement in db.Database.GenerateCreateScript()
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(statement, @"CREATE\s+(?:UNIQUE\s+)?(?<kind>TABLE|INDEX)\s+""(?<name>[^""]+)""",
                RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var name = match.Groups["name"].Value;
            if (existing.Contains(name)) continue;

            if (match.Groups["kind"].Value.Equals("INDEX", StringComparison.OrdinalIgnoreCase))
            {
                deferredIndexes.Add((name, statement)); // create after columns exist
                continue;
            }
            db.Database.ExecuteSqlRaw(statement);
            created.Add(name);
        }

        // Add columns the model gained on tables that already existed.
        // (EnsureCreated/GenerateCreateScript build whole tables but never ALTER them.)
        created.AddRange(AddMissingColumns(db, existing));

        // Now the indexes — their columns are guaranteed to exist.
        foreach (var (name, sql) in deferredIndexes)
        {
            db.Database.ExecuteSqlRaw(sql);
            created.Add(name);
        }
        return created;
    }

    /// <summary>
    /// For each pre-existing table, adds any columns present in the model but missing from
    /// the database. Non-nullable additions get a safe default so ALTER TABLE succeeds.
    /// </summary>
    private static List<string> AddMissingColumns(AppDbContext db, HashSet<string> preExisting)
    {
        var added = new List<string>();

        // Snapshot each pre-existing table's current columns.
        var tableColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        db.Database.OpenConnection();
        try
        {
            foreach (var entityType in db.Model.GetEntityTypes())
            {
                var table = entityType.GetTableName();
                if (table is null || !preExisting.Contains(table) || tableColumns.ContainsKey(table)) continue;

                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var pragma = db.Database.GetDbConnection().CreateCommand();
                pragma.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = pragma.ExecuteReader();
                while (reader.Read()) cols.Add(reader.GetString(1)); // (cid, name, type, notnull, dflt, pk)
                tableColumns[table] = cols;
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null || !tableColumns.TryGetValue(table, out var have)) continue;
            var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());

            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName(storeObject);
                if (column is null || have.Contains(column)) continue;

                var storeType = property.GetColumnType() ?? property.GetRelationalTypeMapping().StoreType;
                var ddl = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {storeType}";
                if (!property.IsNullable) ddl += $" NOT NULL DEFAULT {DefaultLiteral(property.ClrType)}";
                db.Database.ExecuteSqlRaw(ddl);
                added.Add($"{table}.{column}");
            }
        }
        return added;
    }

    private static string DefaultLiteral(Type clrType)
    {
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t == typeof(string)) return "''";
        if (t == typeof(byte[])) return "x''";
        if (t == typeof(bool)) return "0";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "'0001-01-01 00:00:00'";
        if (t == typeof(Guid)) return "''";
        return "0"; // numeric types
    }
}
