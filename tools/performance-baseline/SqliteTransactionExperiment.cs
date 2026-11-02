// SqliteTransactionExperiment.cs
// Synthetic disk-only experiment: never opens the plugin's real database.
using System.Diagnostics;
using System.Globalization;
using Dapper;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Test;
using Microsoft.Data.Sqlite;

internal static class SqliteTransactionExperiment
{
    private const string InsertAsset = "INSERT OR IGNORE INTO asset(data_b64, plain_text_b64, hash_id) VALUES (@DataB64, @PlainTextB64, @HashId);";
    private const string InsertRecord = """
        INSERT OR IGNORE INTO record(hash_id, data_md5_b64, sender_app, cached_image_path,
            data_type, create_time, datetime_score, pinned, encrypt_data, plain_text, encrypt_key_md5)
        VALUES (@HashId, @DataMd5B64, @SenderApp, @CachedImagePath,
            @DataType, @CreateTime, @DatetimeScore, @Pinned, @EncryptData, @PlainText, @EncryptKeyMd5);
        """;

    public static void Run()
    {
        var records = Enumerable.Range(0, 500).Select(CreateRecord).ToArray();
        CheckRollback(records);
        // Warm both paths before alternating the measured variants to reduce ordering bias.
        WriteBatch(records, false);
        WriteBatch(records, true);
        var without = new double[5];
        var with = new double[5];
        for (int i = 0; i < without.Length; i++)
        {
            if (i % 2 == 0)
            {
                without[i] = WriteBatch(records, false);
                with[i] = WriteBatch(records, true);
            }
            else
            {
                with[i] = WriteBatch(records, true);
                without[i] = WriteBatch(records, false);
            }
        }
        Array.Sort(without);
        Array.Sort(with);
        Console.WriteLine($"SQLite transaction experiment 500 records autocommit: ms min/median/max={without[0]:F2}/{without[2]:F2}/{without[4]:F2}");
        Console.WriteLine($"SQLite transaction experiment 500 records transaction: ms min/median/max={with[0]:F2}/{with[2]:F2}/{with[4]:F2}");
        Console.WriteLine("SQLite transaction experiment integrity and rollback checks passed");
    }

    private static ClipboardData CreateRecord(int index) => new($"synthetic database record {index}", DataType.PlainText, false)
    {
        HashId = index.ToString(CultureInfo.InvariantCulture),
        SenderApp = "synthetic.exe",
        InitScore = index,
        CreateTime = new DateTime(2025, 1, 1).AddSeconds(index),
        EncryptKeyMd5 = string.Empty,
        CachedImagePath = string.Empty,
        Pinned = false,
        Saved = true
    };

    private static double WriteBatch(ClipboardData[] records, bool transactional)
    {
        using var database = new TemporaryDatabase();
        var timer = Stopwatch.StartNew();
        using (var transaction = transactional ? database.Connection.BeginTransaction() : null)
        {
            Insert(database.Connection, records, transaction);
            transaction?.Commit();
        }
        timer.Stop();
        // The verification is outside the timer but is required for each sample.
        CheckCounts(database.Connection, records.Length);
        var first = database.Connection.QuerySingle<string>("SELECT data_b64 FROM asset WHERE hash_id=@Id", new { Id = "0" });
        if (first != "synthetic database record 0") throw new InvalidOperationException("Synthetic data mismatch");
        return timer.Elapsed.TotalMilliseconds;
    }

    private static void CheckRollback(ClipboardData[] records)
    {
        using var database = new TemporaryDatabase();
        using (var transaction = database.Connection.BeginTransaction())
        {
            Insert(database.Connection, records.Take(5), transaction);
            transaction.Rollback();
        }
        CheckCounts(database.Connection, 0);
    }

    private static void Insert(SqliteConnection connection, IEnumerable<ClipboardData> records, SqliteTransaction? transaction)
    {
        foreach (var data in records)
        {
            // Same conversions and two SQL statements as SqliteDatabase.AddRecordsAsync.
            connection.Execute(InsertAsset, Asset.FromClipboardData(data, true), transaction);
            connection.Execute(InsertRecord, Record.FromClipboardData(data, true), transaction);
        }
    }

    private static void CheckCounts(SqliteConnection connection, int expected)
    {
        if (connection.ExecuteScalar<int>("SELECT count(*) FROM record") != expected ||
            connection.ExecuteScalar<int>("SELECT count(*) FROM asset") != expected ||
            connection.ExecuteScalar<int>("SELECT count(*) FROM record r LEFT JOIN asset a ON r.hash_id = a.hash_id WHERE a.hash_id IS NULL") != 0)
            throw new InvalidOperationException("Synthetic database integrity check failed");
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"clipboardplus-experiment-{Guid.NewGuid():N}.db");
        private readonly SqliteDatabase database;
        public SqliteConnection Connection => database.Connection;

        public TemporaryDatabase()
        {
            database = new SqliteDatabase(path, new ClipboardPlus());
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
