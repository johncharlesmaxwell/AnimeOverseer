using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AnimeOverseer.Server.Data;

/// <summary>
/// Connection settings that keep the UI responsive while a sync job writes.
/// </summary>
public static class SqliteConnectionSetup
{
    public const string DefaultConnectionString = "Data Source=animeoverseer.db";

    /// <summary>
    /// Shared-cache mode uses table-level locks across connections, which blocks WAL's
    /// concurrent readers behind the job's writes, so it is always turned off. A busy
    /// timeout is applied when none is configured so brief writer contention retries
    /// instead of failing with "database is locked".
    /// </summary>
    public static string Normalize(string? connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(
            string.IsNullOrWhiteSpace(connectionString) ? DefaultConnectionString : connectionString);
        if (builder.Cache == SqliteCacheMode.Shared) builder.Cache = SqliteCacheMode.Default;
        if (!builder.ContainsKey("Default Timeout") && !builder.ContainsKey("Command Timeout"))
            builder.DefaultTimeout = 30;
        return builder.ToString();
    }
}

/// <summary>
/// Applies per-connection pragmas. Unlike journal_mode, synchronous is not stored in the
/// database file, so it must be set each time a connection opens.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    // NORMAL is the recommended setting with WAL: commits skip the per-transaction fsync
    // (the WAL is synced at checkpoints), and the database cannot be corrupted. A power
    // loss may drop only the last few commits.
    private const string Pragmas = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
