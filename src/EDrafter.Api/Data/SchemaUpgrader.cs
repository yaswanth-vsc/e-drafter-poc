using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace EDrafter.Api.Data;

/// <summary>
/// Adds the Zoho Sign columns to a database that already exists.
///
/// EnsureCreated only builds a schema when the database is missing — it never alters an
/// existing one. Without this, an existing edrafter-poc.db (with agreements already
/// holding a paid stamp) would fail on the first query that touches a new column, and the
/// only fix would be deleting the database and the stamps it tracks.
///
/// Every column is nullable, so a plain ADD COLUMN is enough and old rows need no default.
/// Safe to run on every start: each column is checked before it is added.
/// </summary>
public static class SchemaUpgrader
{
    private static readonly (string Table, string Column, string SqliteType, string PostgresType)[] Columns =
    [
        ("Agreements", "SigningProvider", "TEXT", "text"),
        ("Agreements", "FinalPdfPath", "TEXT", "text"),
        ("Agreements", "ZohoRequestId", "TEXT", "text"),
        ("Agreements", "ZohoDocumentId", "TEXT", "text"),
        ("Agreements", "ZohoFieldsPlacedAt", "TEXT", "timestamp with time zone"),
        ("Agreements", "ZohoSubmittedAt", "TEXT", "timestamp with time zone"),
        ("Signatories", "Role", "TEXT", "text"),
        ("Signatories", "SigningOrder", "INTEGER", "integer"),
        ("Signatories", "ZohoActionId", "TEXT", "text"),
    ];

    public static async Task UpgradeAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var postgres = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
            opened = true;
        }

        try
        {
            foreach (var (table, column, sqliteType, postgresType) in Columns)
            {
                if (await ColumnExistsAsync(conn, postgres, table, column, ct)) continue;

                var type = postgres ? postgresType : sqliteType;
                await ExecuteAsync(conn, $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type}", ct);
                logger.LogWarning("Schema upgraded: added {Table}.{Column} ({Type}).", table, column, type);
            }

            // The webhook looks agreements up by Zoho request id.
            await ExecuteAsync(conn,
                "CREATE INDEX IF NOT EXISTS \"IX_Agreements_ZohoRequestId\" ON \"Agreements\" (\"ZohoRequestId\")", ct);
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        DbConnection conn, bool postgres, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = postgres
            ? "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = @t AND column_name = @c"
            : "SELECT COUNT(*) FROM pragma_table_info(@t) WHERE name = @c";

        var t = cmd.CreateParameter();
        t.ParameterName = "@t";
        t.Value = table;
        cmd.Parameters.Add(t);

        var c = cmd.CreateParameter();
        c.ParameterName = "@c";
        c.Value = column;
        cmd.Parameters.Add(c);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    private static async Task ExecuteAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
