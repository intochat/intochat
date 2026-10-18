namespace IntoChat.OS.Postgres;

// Where a deployment places tables that no app slot redirects.
public enum PostgresIsolation { Shared, DatabasePerBrain }
