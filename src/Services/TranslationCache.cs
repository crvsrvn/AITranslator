using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AITranslator.Services;

public sealed class TranslationCache
{
    public const string AiLookupBucket = "lookup-v4";

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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenInitializedConnectionAsync(cancellationToken);
    }

    public async Task<T?> GetAsync<T>(string bucket, string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenInitializedConnectionAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM cache_entries WHERE bucket = $bucket AND cache_key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$key", key);

        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null ? default : JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public async Task SetAsync<T>(string bucket, string key, T value, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);

        await using var connection = await OpenInitializedConnectionAsync(cancellationToken);

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
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAiLookupAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenInitializedConnectionAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cache_entries WHERE bucket = $bucket; VACUUM;";
        command.Parameters.AddWithValue("$bucket", AiLookupBucket);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenInitializedConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

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
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
