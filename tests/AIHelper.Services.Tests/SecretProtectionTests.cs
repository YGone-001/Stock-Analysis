using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using AIHelper.Helpers;
using AIHelper.Models;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SecretProtectionTests
{
    [Fact]
    public void DpapiEnvelope_RoundTripsAndDoesNotExposeCiphertextOnFailure()
    {
        WindowsDpapiSecretProtector protector = new();
        string encrypted = protector.Protect("credential-value");

        Assert.StartsWith(WindowsDpapiSecretProtector.Prefix, encrypted, StringComparison.Ordinal);
        SecretUnprotectResult roundTrip = protector.Unprotect(encrypted);
        Assert.Equal("credential-value", roundTrip.Value);
        Assert.False(roundTrip.Failed);
        Assert.False(roundTrip.RequiresMigration);

        SecretUnprotectResult malformed = protector.Unprotect(WindowsDpapiSecretProtector.Prefix + "not-base64");
        Assert.True(malformed.Failed);
        Assert.Equal(string.Empty, malformed.Value);
    }

    [Fact]
    public void DpapiEnvelope_ReadsLegacyPlaintextOnlyForMigration()
    {
        WindowsDpapiSecretProtector protector = new();
        SecretUnprotectResult legacy = protector.Unprotect("previous-plaintext-value");

        Assert.Equal("previous-plaintext-value", legacy.Value);
        Assert.True(legacy.RequiresMigration);
        Assert.False(legacy.Failed);
    }

    [Fact]
    public void ConfigStore_ProtectsBothSecretsBeforeReplacingConfig()
    {
        using TemporaryConfig temporary = new();
        ConfigStore store = new(temporary.Path, new PrefixProtector());
        AppConfig source = new() { ProxyPassword = "proxy-secret", SavedChatPwd = "chat-secret" };

        Assert.True(store.TrySave(source, out string? error), error);
        string persisted = File.ReadAllText(temporary.Path);
        Assert.DoesNotContain("proxy-secret", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-secret", persisted, StringComparison.Ordinal);
        Assert.Contains(WindowsDpapiSecretProtector.Prefix, persisted, StringComparison.Ordinal);
        Assert.False(File.Exists(temporary.Path + ".tmp"));

        AppConfig loaded = new ConfigStore(temporary.Path, new PrefixProtector()).Load();
        Assert.Equal("proxy-secret", loaded.ProxyPassword);
        Assert.Equal("chat-secret", loaded.SavedChatPwd);
    }

    [Fact]
    public void ConfigStore_MigratesLegacySecretsOnTheNextSuccessfulSave()
    {
        using TemporaryConfig temporary = new();
        AppConfig legacy = new() { ProxyPassword = "legacy-proxy", SavedChatPwd = "legacy-chat" };
        File.WriteAllText(temporary.Path, JsonSerializer.Serialize(legacy));
        ConfigStore store = new(temporary.Path, new PrefixProtector());

        AppConfig loaded = store.Load();
        Assert.Equal("legacy-proxy", loaded.ProxyPassword);
        Assert.Equal("legacy-chat", loaded.SavedChatPwd);
        Assert.True(store.TrySave(loaded, out string? error), error);

        string persisted = File.ReadAllText(temporary.Path);
        Assert.DoesNotContain("legacy-proxy", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-chat", persisted, StringComparison.Ordinal);
        Assert.Contains(WindowsDpapiSecretProtector.Prefix, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigStore_ProtectFailureKeepsPriorConfigAndLeavesNoPlaintext()
    {
        using TemporaryConfig temporary = new();
        const string sentinel = "{\"sentinel\":true}";
        File.WriteAllText(temporary.Path, sentinel);
        ConfigStore store = new(temporary.Path, new FailingProtector());
        AppConfig source = new() { ProxyPassword = "must-not-persist", SavedChatPwd = "also-must-not-persist" };

        Assert.False(store.TrySave(source, out string? error));
        Assert.Equal("Configuration was not saved.", error);
        Assert.Equal(sentinel, File.ReadAllText(temporary.Path));
        Assert.False(File.Exists(temporary.Path + ".tmp"));
        Assert.DoesNotContain("must-not-persist", File.ReadAllText(temporary.Path), StringComparison.Ordinal);
        Assert.DoesNotContain("also-must-not-persist", File.ReadAllText(temporary.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigStore_DecryptionFailureClearsSecretsInsteadOfReturningCiphertext()
    {
        using TemporaryConfig temporary = new();
        AppConfig persisted = new()
        {
            ProxyPassword = WindowsDpapiSecretProtector.Prefix + "unreadable-proxy",
            SavedChatPwd = WindowsDpapiSecretProtector.Prefix + "unreadable-chat"
        };
        File.WriteAllText(temporary.Path, JsonSerializer.Serialize(persisted));

        AppConfig loaded = new ConfigStore(temporary.Path, new UnreadableProtector()).Load();
        Assert.Equal(string.Empty, loaded.ProxyPassword);
        Assert.Equal(string.Empty, loaded.SavedChatPwd);
    }

    private sealed class PrefixProtector : ISecretProtector
    {
        public string Protect(string value) => string.IsNullOrEmpty(value)
            ? string.Empty
            : WindowsDpapiSecretProtector.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        public SecretUnprotectResult Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return new(string.Empty, false, false);
            if (!value.StartsWith(WindowsDpapiSecretProtector.Prefix, StringComparison.Ordinal)) return new(value, true, false);
            return new(Encoding.UTF8.GetString(Convert.FromBase64String(value[WindowsDpapiSecretProtector.Prefix.Length..])), false, false);
        }
    }

    private sealed class FailingProtector : ISecretProtector
    {
        public string Protect(string value) => throw new CryptographicException("test protection failure");
        public SecretUnprotectResult Unprotect(string value) => new(string.Empty, false, true);
    }

    private sealed class UnreadableProtector : ISecretProtector
    {
        public string Protect(string value) => value;
        public SecretUnprotectResult Unprotect(string value) => new(string.Empty, false, true);
    }

    private sealed class TemporaryConfig : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIHelper", "security-tests", Guid.NewGuid().ToString("N"));
        public string Path { get; }

        public TemporaryConfig()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "config.json");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
            catch { }
        }
    }
}
