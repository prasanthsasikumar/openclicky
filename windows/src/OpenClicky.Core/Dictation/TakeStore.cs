// Ports Dictation/TakeStore.swift: every take, on this PC, in SQLite (Microsoft.Data.Sqlite): what
// was heard, what was written, where it went. One connection behind one lock, so there is no
// contention. The store is what the record page's counts, the history page and its search read
// from. The schema (tables, columns, index) is the Mac's, so a dictation.sqlite reads the same on
// both: ids are upper-case UUID strings and dates are seconds since 1970 (REAL).

using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Dictation;

public enum TakeMode { Dictate, Edit, Clipboard }

public enum TakeStatus { Complete, Failed, Cancelled }

public enum TakePasteOutcome { Verified, Posted, LeftInOrb, LeftOnPasteboard, None }

/// <summary>One take, as recorded.</summary>
public sealed record TakeRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public TakeMode Mode { get; init; } = TakeMode.Dictate;
    public TakeStatus Status { get; init; } = TakeStatus.Complete;
    public string RawText { get; init; } = "";
    public string FormattedText { get; init; } = "";
    /// <summary>The app's process name on Windows (a bundle id in a Mac's database). Column: app_bundle_id.</summary>
    public string? AppProcessName { get; init; }
    public string? AppName { get; init; }
    public string? Language { get; init; }
    public string Engine { get; init; } = "";
    public double DurationSeconds { get; init; }
    public TakePasteOutcome PasteOutcome { get; init; } = TakePasteOutcome.None;
    public bool Pinned { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>What history shows: the written text, or the raw words when nothing was written.</summary>
    public string DisplayText => FormattedText.Length == 0 ? RawText : FormattedText;

    public int WordCount => DisplayText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

public sealed class TakeStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The counts the record page shows.</summary>
public sealed record TakeStats(int WordsToday, int TakesToday, int WordsThisWeek, int TakesThisWeek, string? MostUsedAppToday, int AllTimeTakes);

public sealed record AppUsage(string AppProcessName, string? Name, int Count);

public sealed record DayWords(DateTime Day, int Words);

public sealed class TakeStore : IDisposable
{
    /// <summary>%LOCALAPPDATA%\OpenClicky\dictation.sqlite</summary>
    public static string DefaultFilePath => OpenClickyPaths.DictationDatabaseFile;

    /// <summary>
    /// The installed app and a Debug build share this file: a write that meets the other's lock
    /// waits up to two seconds for it instead of failing at once with SQLITE_BUSY.
    /// </summary>
    public const int BusyTimeoutMilliseconds = 2000;

    private readonly object queue = new();
    private readonly SqliteConnection database;

    public string FilePath { get; }

    public TakeStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultFilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = FilePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = BusyTimeoutMilliseconds / 1000,
            }.ToString();
            database = new SqliteConnection(connectionString);
            database.Open();
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new TakeStoreException($"could not open {FilePath}", error);
        }
        Execute($"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}");
        Execute("PRAGMA journal_mode = WAL");
        Execute("PRAGMA foreign_keys = ON");
        Execute("""
        CREATE TABLE IF NOT EXISTS takes (
            id TEXT PRIMARY KEY,
            created_at REAL NOT NULL,
            mode TEXT NOT NULL,
            status TEXT NOT NULL,
            raw_text TEXT NOT NULL,
            formatted_text TEXT NOT NULL,
            app_bundle_id TEXT,
            app_name TEXT,
            language TEXT,
            engine TEXT NOT NULL DEFAULT '',
            duration_seconds REAL NOT NULL DEFAULT 0,
            paste_outcome TEXT NOT NULL DEFAULT 'none',
            pinned INTEGER NOT NULL DEFAULT 0,
            failure_reason TEXT
        )
        """);
        Execute("CREATE INDEX IF NOT EXISTS takes_created_at ON takes (created_at DESC)");
        Execute("""
        CREATE TABLE IF NOT EXISTS take_revisions (
            id TEXT PRIMARY KEY,
            take_id TEXT NOT NULL REFERENCES takes(id) ON DELETE CASCADE,
            created_at REAL NOT NULL,
            previous_text TEXT NOT NULL,
            new_text TEXT NOT NULL,
            editor TEXT NOT NULL
        )
        """);
    }

    public void Dispose()
    {
        lock (queue) database.Dispose();
    }

    // MARK: writes

    public void Insert(TakeRecord take)
    {
        lock (queue)
        {
            using var statement = Prepare("""
            INSERT OR REPLACE INTO takes (id, created_at, mode, status, raw_text, formatted_text, app_bundle_id, app_name, language, engine, duration_seconds, paste_outcome, pinned, failure_reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            """,
                IdText(take.Id), Seconds(take.CreatedAt), take.Mode.RawValue(), take.Status.RawValue(), take.RawText, take.FormattedText,
                take.AppProcessName, take.AppName, take.Language, take.Engine, take.DurationSeconds, take.PasteOutcome.RawValue(),
                take.Pinned ? 1 : 0, take.FailureReason);
            Step(statement);
        }
    }

    /// <summary>Rewrites a take's text (an edit from history or Hey Clicky), keeping the previous text as a revision.</summary>
    public void Revise(Guid takeId, string newText, string editor)
    {
        lock (queue)
        {
            if (FetchLocked(takeId) is not { } current) return;
            using (var revision = Prepare("INSERT INTO take_revisions (id, take_id, created_at, previous_text, new_text, editor) VALUES ($1, $2, $3, $4, $5, $6)",
                       IdText(Guid.NewGuid()), IdText(takeId), Seconds(DateTimeOffset.Now), current.DisplayText, newText, editor))
            {
                Step(revision);
            }
            using var update = Prepare("UPDATE takes SET formatted_text = $1 WHERE id = $2", newText, IdText(takeId));
            Step(update);
        }
    }

    public void SetPinned(bool pinned, Guid takeId)
    {
        lock (queue)
        {
            using var update = Prepare("UPDATE takes SET pinned = $1 WHERE id = $2", pinned ? 1 : 0, IdText(takeId));
            Step(update);
        }
    }

    public void Delete(Guid takeId)
    {
        lock (queue)
        {
            using var statement = Prepare("DELETE FROM takes WHERE id = $1", IdText(takeId));
            Step(statement);
        }
    }

    public void DeleteAll()
    {
        lock (queue)
        {
            Execute("DELETE FROM take_revisions");
            Execute("DELETE FROM takes");
        }
    }

    // MARK: reads

    public TakeRecord? Fetch(Guid id)
    {
        lock (queue) return FetchLocked(id);
    }

    /// <summary>Newest first. `query` matches words in either text, case-insensitively; `mode` narrows the kind.</summary>
    public IReadOnlyList<TakeRecord> Recent(int limit = 500, string? query = null, TakeMode? mode = null, string? appProcessName = null)
    {
        lock (queue)
        {
            var sql = "SELECT * FROM takes WHERE 1 = 1";
            var arguments = new List<object?>();
            if (query is not null && query.Trim(' ', '\t').Length > 0)
            {
                foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    sql += $" AND (raw_text LIKE ${arguments.Count + 1} OR formatted_text LIKE ${arguments.Count + 2})";
                    var pattern = "%" + EscapeLike(word) + "%";
                    arguments.Add(pattern);
                    arguments.Add(pattern);
                }
            }
            if (mode is { } wantedMode)
            {
                sql += $" AND mode = ${arguments.Count + 1}";
                arguments.Add(wantedMode.RawValue());
            }
            if (appProcessName is not null)
            {
                sql += $" AND app_bundle_id = ${arguments.Count + 1}";
                arguments.Add(appProcessName);
            }
            sql += $" ORDER BY created_at DESC LIMIT {Math.Max(1, limit)}";
            using var statement = Prepare(sql, [.. arguments]);
            using var reader = Read(statement);
            var rows = new List<TakeRecord>();
            while (reader.Read()) rows.Add(Row(reader));
            return rows;
        }
    }

    /// <summary>The takes whose text was changed after they were written (history shows them as "edited").</summary>
    public IReadOnlySet<Guid> RevisedTakeIds()
    {
        lock (queue)
        {
            using var statement = Prepare("SELECT DISTINCT take_id FROM take_revisions");
            using var reader = Read(statement);
            var ids = new HashSet<Guid>();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && Guid.TryParse(reader.GetString(0), out var id)) ids.Add(id);
            }
            return ids;
        }
    }

    /// <summary>The apps takes have gone into, most used first.</summary>
    public IReadOnlyList<AppUsage> AppsUsed()
    {
        lock (queue)
        {
            using var statement = Prepare("SELECT app_bundle_id, app_name, COUNT(*) FROM takes WHERE app_bundle_id IS NOT NULL GROUP BY app_bundle_id ORDER BY COUNT(*) DESC");
            using var reader = Read(statement);
            var rows = new List<AppUsage>();
            while (reader.Read())
            {
                rows.Add(new AppUsage(TextOrNull(reader, 0) ?? "", TextOrNull(reader, 1), reader.GetInt32(2)));
            }
            return rows;
        }
    }

    /// <summary>Words dictated on each of the last `days` days, oldest first (today last). Days are local calendar days.</summary>
    public IReadOnlyList<DayWords> WordsPerDay(int days = 7, DateTimeOffset? now = null, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var startOfToday = StartOfDay(now ?? DateTimeOffset.Now, timeZone);
        var firstDay = startOfToday.AddDays(-(days - 1));
        var takes = Recent(limit: 20000).Where(take => LocalTime(take.CreatedAt, timeZone) >= firstDay && take.Mode != TakeMode.Clipboard && take.Status == TakeStatus.Complete);
        var byDay = new Dictionary<DateTime, int>();
        foreach (var take in takes)
        {
            var day = LocalTime(take.CreatedAt, timeZone).Date;
            byDay[day] = byDay.GetValueOrDefault(day) + take.WordCount;
        }
        return Enumerable.Range(0, days).Select(offset =>
        {
            var day = firstDay.AddDays(offset);
            return new DayWords(day, byDay.GetValueOrDefault(day));
        }).ToList();
    }

    public TakeStats Stats(DateTimeOffset? now = null, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var startOfToday = StartOfDay(now ?? DateTimeOffset.Now, timeZone);
        var startOfWeek = startOfToday.AddDays(-6);
        bool Counts(TakeRecord take) => take.Mode != TakeMode.Clipboard && take.Status == TakeStatus.Complete;
        var today = Recent(limit: 5000).Where(take => LocalTime(take.CreatedAt, timeZone) >= startOfToday && Counts(take)).ToList();
        var week = Recent(limit: 20000).Where(take => LocalTime(take.CreatedAt, timeZone) >= startOfWeek && Counts(take)).ToList();
        var mostUsedApp = today
            .Select(take => take.AppName ?? take.AppProcessName)
            .OfType<string>()
            .GroupBy(name => name)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        int allTime;
        lock (queue)
        {
            using var statement = Prepare("SELECT COUNT(*) FROM takes WHERE mode != 'clipboard'");
            allTime = Convert.ToInt32(statement.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }
        return new TakeStats(
            WordsToday: today.Sum(take => take.WordCount),
            TakesToday: today.Count,
            WordsThisWeek: week.Sum(take => take.WordCount),
            TakesThisWeek: week.Count,
            MostUsedAppToday: mostUsedApp,
            AllTimeTakes: allTime);
    }

    // MARK: sqlite plumbing

    private static DateTime LocalTime(DateTimeOffset date, TimeZoneInfo timeZone) => TimeZoneInfo.ConvertTime(date, timeZone).DateTime;

    private static DateTime StartOfDay(DateTimeOffset date, TimeZoneInfo timeZone) => LocalTime(date, timeZone).Date;

    /// <summary>A UUID as Foundation's uuidString writes it.</summary>
    private static string IdText(Guid id) => id.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static double Seconds(DateTimeOffset date) => (date - DateTimeOffset.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;

    private static DateTimeOffset Date(double seconds) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)).ToLocalTime();

    private TakeRecord? FetchLocked(Guid id)
    {
        using var statement = Prepare("SELECT * FROM takes WHERE id = $1", IdText(id));
        using var reader = Read(statement);
        return reader.Read() ? Row(reader) : null;
    }

    private static TakeRecord Row(SqliteDataReader reader) => new()
    {
        Id = Guid.TryParse(TextOrNull(reader, 0), out var id) ? id : Guid.NewGuid(),
        CreatedAt = Date(reader.IsDBNull(1) ? 0 : reader.GetDouble(1)),
        Mode = RawValues.FromRawValue<TakeMode>(TextOrNull(reader, 2)) ?? TakeMode.Dictate,
        Status = RawValues.FromRawValue<TakeStatus>(TextOrNull(reader, 3)) ?? TakeStatus.Complete,
        RawText = TextOrNull(reader, 4) ?? "",
        FormattedText = TextOrNull(reader, 5) ?? "",
        AppProcessName = TextOrNull(reader, 6),
        AppName = TextOrNull(reader, 7),
        Language = TextOrNull(reader, 8),
        Engine = TextOrNull(reader, 9) ?? "",
        DurationSeconds = reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
        PasteOutcome = RawValues.FromRawValue<TakePasteOutcome>(TextOrNull(reader, 11)) ?? TakePasteOutcome.None,
        Pinned = !reader.IsDBNull(12) && reader.GetInt64(12) != 0,
        FailureReason = TextOrNull(reader, 13),
    };

    private static string? TextOrNull(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    /// <summary>LIKE wildcards typed into the search box are not wildcards to the user; they are dropped.</summary>
    private static string EscapeLike(string word) => word.Replace("%", "").Replace("_", "");

    private SqliteCommand Prepare(string sql, params object?[] arguments)
    {
        var command = database.CreateCommand();
        command.CommandText = sql;
        for (var index = 0; index < arguments.Length; index++)
        {
            command.Parameters.AddWithValue($"${index + 1}", arguments[index] ?? DBNull.Value);
        }
        return command;
    }

    private static void Step(SqliteCommand statement)
    {
        try { statement.ExecuteNonQuery(); }
        catch (SqliteException error) { throw new TakeStoreException($"sqlite step failed: {error.Message}", error); }
    }

    private static SqliteDataReader Read(SqliteCommand statement)
    {
        try { return statement.ExecuteReader(); }
        catch (SqliteException error) { throw new TakeStoreException($"sqlite prepare failed: {error.Message}, {Prefix(statement.CommandText)}", error); }
    }

    private void Execute(string sql)
    {
        try
        {
            using var command = database.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        catch (SqliteException error)
        {
            throw new TakeStoreException($"sqlite exec failed: {error.Message}, {Prefix(sql)}", error);
        }
    }

    private static string Prefix(string sql) => sql.Length <= 80 ? sql : sql[..80];
}
