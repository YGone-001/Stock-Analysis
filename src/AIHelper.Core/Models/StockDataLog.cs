using System;
using System.Globalization;
using System.IO;
using System.Text;
using AIHelper.Helpers;
using Serilog;

#pragma warning disable CS8625
#pragma warning disable CS8625
namespace AIHelper.Services.StockData;

public static class StockDataLog
{
	private static readonly object SyncRoot = new object();
	private static bool _directoryInitialized;

	public static void Write(string endpoint, string code, string url, Exception exception, bool cacheUsed, string note = null)
	{
		try
		{
			string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
			if (!_directoryInitialized)
			{
				Directory.CreateDirectory(directory);
				_directoryInitialized = true;
			}
			string path = Path.Combine(directory, "network-" + DateTime.UtcNow.AddHours(8).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
			string exceptionSummary = exception == null ? "-" : exception.GetType().Name + ": " + exception.Message;
			string line = DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
				+ "\tEndpoint=" + Clean(endpoint)
				+ "\tCode=" + Clean(code)
				+ "\tPublicUrl=" + Clean(url)
				+ "\tException=" + Clean(exceptionSummary)
				+ "\tCacheUsed=" + cacheUsed.ToString(CultureInfo.InvariantCulture)
				+ "\tNote=" + Clean(note)
				+ Environment.NewLine;
			lock (SyncRoot)
			{
				File.AppendAllText(path, line, Encoding.UTF8);
			}
		}
		catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
	}

	private static string Clean(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
	}
}
