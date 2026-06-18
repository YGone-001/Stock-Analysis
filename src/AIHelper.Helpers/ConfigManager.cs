using System;
using System.IO;
using System.Text.Json;
using AIHelper.Models;

namespace AIHelper.Helpers;

internal class ConfigManager
{
	private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

	public static AppConfig Load()
	{
		if (!File.Exists(ConfigPath))
		{
			return new AppConfig();
		}
		try
		{
			return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
		}
		catch
		{
			return new AppConfig();
		}
	}

	public static void Save(AppConfig config)
	{
		try
		{
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				WriteIndented = true
			};
			string contents = JsonSerializer.Serialize(config, options);
			File.WriteAllText(ConfigPath, contents);
		}
		catch
		{
		}
	}
}
