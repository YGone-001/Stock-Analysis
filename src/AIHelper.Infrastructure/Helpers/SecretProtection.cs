using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace AIHelper.Helpers;

public sealed record SecretUnprotectResult(string Value, bool RequiresMigration, bool Failed);

/// <summary>Infrastructure-owned secret boundary. Implementations never return plaintext after protection failure.</summary>
public interface ISecretProtector
{
    string Protect(string value);
    SecretUnprotectResult Unprotect(string value);
}

public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    public const string Prefix = "dpapi:v1:";

    public string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DPAPI protection failed; configuration persistence was refused.");
            throw new CryptographicException("Unable to protect a configuration secret.", ex);
        }
    }

    public SecretUnprotectResult Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return new(value, false, false);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return new(value, true, false);
        try
        {
            string encoded = value[Prefix.Length..];
            byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(encoded), null, DataProtectionScope.CurrentUser);
            return new(Encoding.UTF8.GetString(decrypted), false, false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DPAPI unprotection failed; secret was cleared rather than reused.");
            return new(string.Empty, false, true);
        }
    }
}
