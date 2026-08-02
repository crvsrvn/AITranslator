using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AITranslator.Services;

public sealed class SecretStore
{
    internal const string LegacyProfileId = "__legacy__";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AITranslator.ApiKey.v1");
    private static readonly byte[][] LegacyEntropies =
    [
        Convert.FromBase64String("RnJlZVRyYW5zbGF0b3IuQXBpS2V5LnYx"),
        Convert.FromBase64String("TGluZ29EZXNrLkFwaUtleS52MQ==")
    ];
    private readonly AppPaths _paths;

    public SecretStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<string> ReadApiKeyAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var keys = await ReadApiKeysAsync(cancellationToken);
        if (keys.TryGetValue(profileId, out var apiKey))
        {
            return apiKey;
        }

        return keys.TryGetValue(LegacyProfileId, out var legacyApiKey) ? legacyApiKey : string.Empty;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadApiKeysAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ApiKeyFile))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var protectedBytes = await File.ReadAllBytesAsync(_paths.ApiKeyFile, cancellationToken);
        var plainBytes = Unprotect(protectedBytes);
        var payload = Encoding.UTF8.GetString(plainBytes);
        try
        {
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(payload);
            if (keys is not null)
            {
                return new Dictionary<string, string>(keys, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // 旧版本文件只包含单个明文密钥，读取后在下次保存时迁移。
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [LegacyProfileId] = payload
        };
    }

    public async Task SaveApiKeysAsync(IReadOnlyDictionary<string, string> apiKeys,
        CancellationToken cancellationToken = default)
    {
        var normalized = apiKeys
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(item => item.Key, item => item.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        normalized.Remove(LegacyProfileId);

        if (normalized.Count == 0)
        {
            if (File.Exists(_paths.ApiKeyFile))
            {
                File.Delete(_paths.ApiKeyFile);
            }

            return;
        }

        var plainBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized));
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        var temporaryFile = _paths.ApiKeyFile + ".tmp";
        await File.WriteAllBytesAsync(temporaryFile, protectedBytes, cancellationToken);
        File.Move(temporaryFile, _paths.ApiKeyFile, true);
    }

    private static byte[] Unprotect(byte[] protectedBytes)
    {
        try
        {
            return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            foreach (var legacyEntropy in LegacyEntropies)
            {
                try
                {
                    return ProtectedData.Unprotect(protectedBytes, legacyEntropy, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException)
                {
                    // 继续尝试更早版本的熵。
                }
            }

            throw;
        }
    }
}
