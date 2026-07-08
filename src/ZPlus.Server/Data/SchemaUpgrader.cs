using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

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

        // EF's full CREATE script for the model; run only the statements whose object is absent.
        var created = new List<string>();
        foreach (var statement in db.Database.GenerateCreateScript()
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(statement, @"CREATE\s+(?:UNIQUE\s+)?(?:TABLE|INDEX)\s+""(?<name>[^""]+)""",
                RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var name = match.Groups["name"].Value;
            if (existing.Contains(name)) continue;

            db.Database.ExecuteSqlRaw(statement);
            created.Add(name);
        }
        return created;
    }
}
