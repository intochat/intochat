using Npgsql;

namespace IntoChat.OS.Modules.Postgres;

/// <summary>
/// Classifies Postgres and Npgsql exceptions into uniform domain exceptions.
/// </summary>
internal static class PostgresExceptionClassifier
{
    /// <summary>
    /// Classifies an exception into either a <see cref="PostgresUnavailableException"/> (for transient/connection failures)
    /// or a <see cref="PostgresQueryException"/> (for schema/query/constraint refusals).
    /// If the exception is already one of these domain exceptions or an unrelated exception, handles accordingly.
    /// </summary>
    public static Exception Classify(Exception exception, string? customUnavailableMessage = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is PostgresUnavailableException or PostgresQueryException)
        {
            return exception;
        }

        if (exception is PostgresException pgEx)
        {
            return ClassifyPostgresException(pgEx, customUnavailableMessage);
        }

        if (exception is NpgsqlException)
        {
            return new PostgresUnavailableException(
                customUnavailableMessage ?? "Postgres is unreachable or the database connection failed.");
        }

        return exception;
    }

    /// <summary>
    /// Translates a <see cref="PostgresException"/> based on its SQLSTATE code.
    /// Connection-phase and server-lifecycle failures (08*, 28*, 3D000, 57P0*, 53300) mean the placement/server
    /// is unreachable or refusing connections -> <see cref="PostgresUnavailableException"/> (HTTP 503).
    /// All other Postgres errors (syntax, constraints, permissions within an active connection) translate to
    /// <see cref="PostgresQueryException"/> (HTTP 409).
    /// </summary>
    public static Exception ClassifyPostgresException(PostgresException error, string? customUnavailableMessage = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var sqlState = error.SqlState;
        if (IsUnavailableSqlState(sqlState))
        {
            return new PostgresUnavailableException(
                customUnavailableMessage ?? "This table's database is unreachable or refused the connection.");
        }

        return new PostgresQueryException($"PostgreSQL refused the query (SQLSTATE {error.SqlState}).");
    }

    /// <summary>
    /// Determines whether a SQLSTATE code represents an unavailable/transient condition.
    /// </summary>
    public static bool IsUnavailableSqlState(string? sqlState)
    {
        if (string.IsNullOrEmpty(sqlState))
        {
            return false;
        }

        return sqlState.StartsWith("08", StringComparison.Ordinal)     // Connection Exception (08000, 08003, 08006, etc.)
            || sqlState.StartsWith("28", StringComparison.Ordinal)     // Invalid Authorization Specification (28000, 28P01, etc.)
            || sqlState == "3D000"                                     // Invalid Catalog Name (database does not exist)
            || sqlState.StartsWith("57P0", StringComparison.Ordinal)   // Operator Intervention: 57P01 admin shutdown, 57P02 crash shutdown, 57P03 cannot connect now
            || sqlState == "53300";                                    // Too Many Connections
    }
}
