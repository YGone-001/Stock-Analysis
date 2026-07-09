using System;
using AIHelper.Models;

namespace AIHelper.Helpers;

public static class ChatServiceConfig
{
	public static bool IsEnabled
	{
		get
		{
			AppConfig config = ConfigManager.Load();
			return config.IsChatEnabled && Uri.TryCreate(config.ChatServerUrl, UriKind.Absolute, out _);
		}
	}

	public static string BaseUrl
	{
		get
		{
			AppConfig config = ConfigManager.Load();
			return config.IsChatEnabled ? (config.ChatServerUrl ?? "").TrimEnd('/') : "";
		}
	}

	public static string BuildUrl(string path)
	{
		string baseUrl = BaseUrl;
		if (string.IsNullOrWhiteSpace(baseUrl))
		{
			throw new InvalidOperationException("聊天服务未启用。请先在配置文件中设置 IsChatEnabled 和 ChatServerUrl。");
		}
		return baseUrl + (path.StartsWith('/') ? path : "/" + path);
	}
}
