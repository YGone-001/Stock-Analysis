using System.Text;
using System.Text.Json;
using AIHelper.Models;
using Serilog;

namespace AIHelper.Helpers;

/// <summary>Persists a fully protected copy only after every secret has been protected successfully.</summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly ISecretProtector _secrets;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private AppConfig? _cachedConfig;
    private DateTime _lastReadTime;

    public ConfigStore(string path, ISecretProtector? secrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _secrets = secrets ?? new WindowsDpapiSecretProtector();
    }

    public AppConfig Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return new AppConfig();
            try
            {
                DateTime lastWrite = File.GetLastWriteTimeUtc(_path);
                if (_cachedConfig is not null && _lastReadTime >= lastWrite) return _cachedConfig;
                AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path)) ?? new AppConfig();
                config.SavedChatPwd = ReadSecret(config.SavedChatPwd, "chat password");
                config.ProxyPassword = ReadSecret(config.ProxyPassword, "proxy password");
                _cachedConfig = config;
                _lastReadTime = lastWrite;
                return config;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Configuration load failed; default configuration will be used.");
                return new AppConfig();
            }
        }
    }

    public bool TrySave(AppConfig config, out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_lock)
        {
            string tempPath = _path + ".tmp";
            try
            {
                AppConfig persisted = Clone(config);
                // Both values are protected before a byte is written, so a later protection failure cannot create a half-secured file.
                persisted.ProxyPassword = _secrets.Protect(persisted.ProxyPassword);
                persisted.SavedChatPwd = _secrets.Protect(persisted.SavedChatPwd);
                string directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                byte[] contents = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(persisted, _jsonOptions));
                using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(contents);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tempPath, _path, overwrite: true);
                _cachedConfig = config;
                _lastReadTime = File.GetLastWriteTimeUtc(_path);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                DeleteTemp(tempPath);
                error = "Configuration was not saved.";
                Log.Warning(ex, "Configuration persistence failed; prior configuration was retained.");
                return false;
            }
        }
    }

    private string ReadSecret(string value, string category)
    {
        SecretUnprotectResult result = _secrets.Unprotect(value);
        if (result.Failed) Log.Warning("Stored {SecretCategory} could not be decrypted and was cleared.", category);
        return result.Value;
    }
    private AppConfig Clone(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, _jsonOptions), _jsonOptions) ?? throw new JsonException("Configuration clone failed.");
    private static void DeleteTemp(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
