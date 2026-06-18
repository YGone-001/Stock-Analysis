using System;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using HandyControl.Controls;

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
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(3, 2);
			defaultInterpolatedStringHandler.AppendFormatted("https://www.ooppp.com/soft/aihelper.json");
			defaultInterpolatedStringHandler.AppendLiteral("?t=");
			defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now.Ticks);
			string requestUri = defaultInterpolatedStringHandler.ToStringAndClear();
			UpdateInfoModel updateInfo = JsonSerializer.Deserialize<UpdateInfoModel>(await client.GetStringAsync(requestUri));
			if (updateInfo == null || !IsNewerVersion(updateInfo.version, "1.4.5"))
			{
				return;
			}
			Application.Current.Dispatcher.Invoke(delegate
			{
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(27, 2);
				defaultInterpolatedStringHandler2.AppendLiteral("发现新版本：V");
				defaultInterpolatedStringHandler2.AppendFormatted(updateInfo.version);
				defaultInterpolatedStringHandler2.AppendLiteral("\n\n【更新内容】\n");
				defaultInterpolatedStringHandler2.AppendFormatted(updateInfo.description);
				defaultInterpolatedStringHandler2.AppendLiteral("\n\n是否立即前往下载？");
				if (HandyControl.Controls.MessageBox.Show(defaultInterpolatedStringHandler2.ToStringAndClear(), "\ud83c\udf89 发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Asterisk) == MessageBoxResult.Yes)
				{
					OpenUrl(updateInfo.url);
				}
			});
		}
		catch (Exception)
		{
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
			});
		}
		catch (Exception ex)
		{
			Growl.Error("浏览器拉起失败: " + ex.Message);
		}
	}
}
