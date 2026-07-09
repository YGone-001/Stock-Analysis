using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using HandyControl.Controls;
using Serilog;

namespace AIHelper.Helpers;

public static class UpdateHelper
{
	public const string CurrentVersion = "1.4.5";

	private const string UPDATE_API_URL = "https://www.ooppp.com/soft/aihelper.json";

	public static async Task CheckUpdateAsync()
	{
		try
		{
			using HttpClient client = new HttpClient();
			client.Timeout = TimeSpan.FromSeconds(3.0);
			string requestUri = $"{UPDATE_API_URL}?t={DateTime.Now.Ticks}";
			UpdateInfoModel updateInfo = JsonSerializer.Deserialize<UpdateInfoModel>(await client.GetStringAsync(requestUri));
			if (updateInfo == null || !IsNewerVersion(updateInfo.Version, CurrentVersion))
			{
				return;
			}
			Application.Current.Dispatcher.Invoke(delegate
			{
				string prompt = $"发现新版本：V{updateInfo.Version}\n\n【更新内容】\n{updateInfo.Description}\n\n是否立即前往下载？";
				if (HandyControl.Controls.MessageBox.Show(prompt, "\ud83c\udf89 发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Asterisk) == MessageBoxResult.Yes)
				{
					OpenUrl(updateInfo.Url);
				}
			});
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Swallowed exception");
		}
	}

	private static bool IsNewerVersion(string onlineVersion, string localVersion)
	{
		if (Version.TryParse(onlineVersion, out var result) && Version.TryParse(localVersion, out var result2))
		{
			return result > result2;
		}
		return false;
	}

	private static void OpenUrl(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true
			})?.Dispose();
		}
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			Growl.Error("浏览器拉起失败: " + ex.Message);
		}
	}
}
