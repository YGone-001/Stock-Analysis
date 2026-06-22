using System;
using System.Globalization;
using System.IO;
using System.Text;
using AIHelper.Helpers;

namespace AIHelper.Services.StockData;

internal static class StockDataLog
{
	private static readonly object SyncRoot = new object();

	public static void Write(string endpoint, string code, string url, Exception exception, bool cacheUsed, string note = null)
	{
		try
		{
			string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
			Directory.CreateDirectory(directory);
			string path = Path.Combine(directory, "network-" + TimeHelper.BeijingNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
			string exceptionSummary = exception == null ? "-" : exception.GetType().Name + ": " + exception.Message;
			string line = TimeHelper.BeijingNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
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
		catch
		{
		}
	}

	private static string Clean(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
	}
}
