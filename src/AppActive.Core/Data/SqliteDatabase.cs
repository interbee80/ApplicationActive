using Microsoft.Data.Sqlite;

namespace AppActive.Core.Data;

/// <summary>Фабрика подключений и инициализация схемы SQLite.</summary>
public sealed class SqliteDatabase
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString;

    public SqliteDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Путь к базе не задан", nameof(databasePath));
        Path = System.IO.Path.GetFullPath(databasePath);
        Directory = System.IO.Path.GetDirectoryName(Path) ?? ".";
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true
        }.ToString();
    }

    public string Path { get; }

    public string Directory { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        System.IO.Directory.CreateDirectory(Directory);
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = Schema1 + """
            INSERT INTO SchemaInfo(Key, Value) VALUES('Version', '1')
              ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.ExecuteNonQuery();
    }

    public long GetFileSizeBytes()
    {
        try
        {
            var info = new FileInfo(Path);
            var total = info.Exists ? info.Length : 0;
            var wal = new FileInfo(Path + "-wal");
            if (wal.Exists) total += wal.Length;
            return total;
        }
        catch
        {
            return 0;
        }
    }

    public long GetWalSizeBytes()
    {
        try
        {
            var wal = new FileInfo(Path + "-wal");
            return wal.Exists ? wal.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private const string Schema1 = """
        CREATE TABLE IF NOT EXISTS SchemaInfo (
            Key   TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Applications (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            AppKey       TEXT NOT NULL UNIQUE,
            ExeName      TEXT NOT NULL,
            ExePath      TEXT,
            FriendlyName TEXT,
            Category     TEXT,
            IsIgnored    INTEGER NOT NULL DEFAULT 0,
            FirstSeenUtc TEXT NOT NULL,
            LastSeenUtc  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Applications_ExeName  ON Applications(ExeName);
        CREATE INDEX IF NOT EXISTS IX_Applications_LastSeen ON Applications(LastSeenUtc);

        CREATE TABLE IF NOT EXISTS ActivityIntervals (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            ApplicationId   INTEGER NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
            SessionId       INTEGER NOT NULL,
            UserName        TEXT NOT NULL,
            DayKey          TEXT NOT NULL,
            HourIndex       INTEGER NOT NULL,
            StartUtc        TEXT NOT NULL,
            EndUtc          TEXT NOT NULL,
            DurationSeconds REAL NOT NULL DEFAULT 0,
            WindowTitle     TEXT,
            UpdatedUtc      TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Intervals_Day_App   ON ActivityIntervals(DayKey, ApplicationId);
        CREATE INDEX IF NOT EXISTS IX_Intervals_Day_User  ON ActivityIntervals(DayKey, UserName);
        CREATE INDEX IF NOT EXISTS IX_Intervals_Day_Start ON ActivityIntervals(DayKey, StartUtc);
        CREATE INDEX IF NOT EXISTS IX_Intervals_Lookup    ON ActivityIntervals(ApplicationId, SessionId, UserName, DayKey, HourIndex, EndUtc);

        CREATE TABLE IF NOT EXISTS Groups (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Name        TEXT NOT NULL UNIQUE,
            Description TEXT,
            Color       TEXT,
            CreatedUtc  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS GroupMembers (
            GroupId       INTEGER NOT NULL REFERENCES Groups(Id) ON DELETE CASCADE,
            ApplicationId INTEGER NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE,
            PRIMARY KEY (GroupId, ApplicationId)
        );

        CREATE TABLE IF NOT EXISTS CriticalRules (
            Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            Name               TEXT NOT NULL,
            TargetType         TEXT NOT NULL,
            ApplicationId      INTEGER REFERENCES Applications(Id) ON DELETE CASCADE,
            GroupId            INTEGER REFERENCES Groups(Id) ON DELETE CASCADE,
            DailyLimitSeconds  REAL NOT NULL,
            Enabled            INTEGER NOT NULL DEFAULT 1,
            Severity           TEXT NOT NULL DEFAULT 'warning',
            DaysOfWeek         TEXT,
            CreatedUtc         TEXT NOT NULL,
            UpdatedUtc         TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS WorkflowRules (
            Id                INTEGER PRIMARY KEY AUTOINCREMENT,
            Name              TEXT NOT NULL,
            Enabled           INTEGER NOT NULL DEFAULT 1,
            TriggerType       TEXT NOT NULL,
            CriticalRuleId    INTEGER REFERENCES CriticalRules(Id) ON DELETE CASCADE,
            ScheduleMinutes   INTEGER NOT NULL DEFAULT 5,
            TargetType        TEXT,
            TargetId          INTEGER,
            ThresholdSeconds  REAL,
            ActionType        TEXT NOT NULL DEFAULT 'event',
            ActionTarget      TEXT,
            CooldownMinutes   INTEGER NOT NULL DEFAULT 60,
            LastRunUtc        TEXT,
            LastStatus        TEXT,
            TriggerCount      INTEGER NOT NULL DEFAULT 0,
            CreatedUtc        TEXT NOT NULL,
            UpdatedUtc        TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS WorkflowEvents (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            OccurredUtc   TEXT NOT NULL,
            DayKey        TEXT NOT NULL,
            SourceType    TEXT NOT NULL,
            SourceId      INTEGER,
            RuleName      TEXT NOT NULL,
            Severity      TEXT NOT NULL,
            Message       TEXT NOT NULL,
            TargetName    TEXT,
            UsedSeconds   REAL,
            LimitSeconds  REAL,
            ActionType    TEXT,
            ActionStatus  TEXT,
            DedupKey      TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_Events_Day  ON WorkflowEvents(DayKey, OccurredUtc);
        CREATE INDEX IF NOT EXISTS IX_Events_Dedup ON WorkflowEvents(SourceType, SourceId, DedupKey);

        CREATE TABLE IF NOT EXISTS Sessions (
            SessionId   INTEGER PRIMARY KEY,
            UserName    TEXT NOT NULL,
            Domain      TEXT,
            StartedUtc  TEXT NOT NULL,
            LastSeenUtc TEXT NOT NULL,
            State       TEXT
        );

        CREATE TABLE IF NOT EXISTS Settings (
            Key        TEXT PRIMARY KEY,
            Value      TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL
        );
        """;
}
