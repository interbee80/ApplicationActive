using Microsoft.Data.Sqlite;

namespace AppActive.Core.Data;

/// <summary>Мелкие помощники для работы с SQLite без лишнего кода в репозиториях.</summary>
internal static class Db
{
    public static SqliteCommand Cmd(SqliteConnection cn, SqliteTransaction? tx, string sql, (string, object?)[] args)
    {
        var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }

    public static int Exec(SqliteConnection cn, string sql, params (string, object?)[] args) => Exec(cn, null, sql, args);

    public static int Exec(SqliteConnection cn, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(cn, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection cn, string sql, params (string, object?)[] args) => Scalar(cn, null, sql, args);

    public static object? Scalar(SqliteConnection cn, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(cn, tx, sql, args);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    public static long Long(SqliteConnection cn, string sql, params (string, object?)[] args) =>
        Scalar(cn, null, sql, args) is { } v ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : 0L;

    public static double Double(SqliteConnection cn, string sql, params (string, object?)[] args) =>
        Scalar(cn, null, sql, args) is { } v ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : 0d;

    public static List<T> Query<T>(
        SqliteConnection cn,
        SqliteTransaction? tx,
        string sql,
        Func<SqliteDataReader, T> map,
        params (string, object?)[] args)
    {
        using var cmd = Cmd(cn, tx, sql, args);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    public static List<T> Query<T>(SqliteConnection cn, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args) =>
        Query(cn, null, sql, map, args);

    public static long LastRowId(SqliteConnection cn, SqliteTransaction? tx)
    {
        using var cmd = Cmd(cn, tx, "SELECT last_insert_rowid();", Array.Empty<(string, object?)>());
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    public static string? StrOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static long Lng(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);

    public static long? LngOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static double Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0d : r.GetDouble(i);

    public static double? DblOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static bool Bool(SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetInt64(i) != 0;

    public static int Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : (int)r.GetInt64(i);
}
