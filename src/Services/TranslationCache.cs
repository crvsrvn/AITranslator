using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AITranslator.Services;

public sealed class TranslationCache
{
    public const string AiLookupBucket = "lookup-v5";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly string _databasePath;

    public TranslationCache(AppPaths paths)
    {
        _databasePath = paths.CacheDatabase;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    // Microsoft.Data.Sqlite 不支持异步 I/O（其 *Async 方法实际同步执行），以下操作统一放到线程池以免阻塞 UI 线程。
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = OpenInitializedConnection();
        }, cancellationToken);

    public Task<T?> GetAsync<T>(string bucket, string key, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = OpenInitializedConnection();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM cache_entries WHERE bucket = $bucket AND cache_key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$bucket", bucket);
            command.Parameters.AddWithValue("$key", key);

            var payload = command.ExecuteScalar() as string;
            return payload is null ? default : JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }, cancellationToken);

    public Task SetAsync<T>(string bucket, string key, T value, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);

        return Task.Run(() =>
        {
            using var connection = OpenInitializedConnection();

            var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO cache_entries (bucket, cache_key, payload, created_utc)
                                  VALUES ($bucket, $key, $payload, $createdUtc)
                                  ON CONFLICT(bucket, cache_key) DO UPDATE SET
                                      payload = excluded.payload,
                                      created_utc = excluded.created_utc;
                                  """;
            command.Parameters.AddWithValue("$bucket", bucket);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public Task ClearAiLookupAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = OpenInitializedConnection();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM cache_entries WHERE bucket = $bucket; VACUUM;";
            command.Parameters.AddWithValue("$bucket", AiLookupBucket);
            command.ExecuteNonQuery();
        }, cancellationToken);

    private SqliteConnection OpenInitializedConnection()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = """
                                  PRAGMA journal_mode = WAL;
                                  CREATE TABLE IF NOT EXISTS cache_entries (
                                      bucket TEXT NOT NULL,
                                      cache_key TEXT NOT NULL,
                                      payload TEXT NOT NULL,
                                      created_utc TEXT NOT NULL,
                                      PRIMARY KEY (bucket, cache_key)
                                  );
                                  DELETE FROM cache_entries WHERE bucket <> $aiLookupBucket;
                                  """;
            command.Parameters.AddWithValue("$aiLookupBucket", AiLookupBucket);
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
