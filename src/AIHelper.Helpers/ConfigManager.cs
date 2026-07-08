using System;
using System.IO;
using System.Text.Json;
using AIHelper.Models;
using System.Security.Cryptography;
using System.Text;

namespace AIHelper.Helpers;

internal class ConfigManager
{
	private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
	private static readonly object _lock = new object();
	private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };

	private static AppConfig _cachedConfig;
	private static DateTime _lastReadTime;


	private static string Protect(string plainText)
	{
		if (string.IsNullOrEmpty(plainText)) return plainText;
		try
		{
			byte[] bytes = Encoding.UTF8.GetBytes(plainText);
			byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
			return Convert.ToBase64String(encrypted);
		}
		catch { return plainText; }
	}

	private static string Unprotect(string cipherText)
	{
		if (string.IsNullOrEmpty(cipherText)) return cipherText;
		try
		{
			byte[] bytes = Convert.FromBase64String(cipherText);
			byte[] decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
			return Encoding.UTF8.GetString(decrypted);
		}
		catch { return cipherText; }
	}

	public static AppConfig Load()
	{
		lock (_lock)
		{
			if (!File.Exists(ConfigPath))
			{
				return new AppConfig();
			}
			try
			{
				DateTime lastWrite = File.GetLastWriteTimeUtc(ConfigPath);
				if (_cachedConfig != null && _lastReadTime >= lastWrite)
				{
					return _cachedConfig;
				}
				_cachedConfig = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
				_cachedConfig.SavedChatPwd = Unprotect(_cachedConfig.SavedChatPwd);
				_cachedConfig.ProxyPassword = Unprotect(_cachedConfig.ProxyPassword);
				_lastReadTime = lastWrite;
				return _cachedConfig;
			}
			catch
			{
				return new AppConfig();
			}
		}
	}

	public static void Save(AppConfig config)
	{
		lock (_lock)
		{
			try
			{
				AppConfig copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, _jsonOptions));
				copy.SavedChatPwd = Protect(copy.SavedChatPwd);
				copy.ProxyPassword = Protect(copy.ProxyPassword);
				string contents = JsonSerializer.Serialize(copy, _jsonOptions);
				string tempPath = ConfigPath + ".tmp";
				File.WriteAllText(tempPath, contents);
				File.Move(tempPath, ConfigPath, overwrite: true);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("Failed to save config: " + ex);
			}
		}
	}
}
