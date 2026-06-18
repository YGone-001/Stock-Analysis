using System;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace AIHelper.Helpers;

public static class AnalyticsService
{
	public static class Actions
	{
		public const string None = "0";

		public const string Startup = "1";

		public const string ViewChart = "2";

		public const string EnterChat = "3";

		public const string ExportData = "4";

		public const string SwitchRoom = "5";

		public const string Add = "6";

		public const string Del = "7";

		public const string Move = "8";

		public const string Sparrow = "9";

		public const string Get = "10";

		public const string Swich = "11";

		public const string Setting = "12";

		public const string Compress = "13";

		public const string Wencai = "14";

		public const string Pos = "15";

		public const string GP = "16";
	}

	private static readonly HttpClient _client = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(2.0)
	};

	private const string TRACKING_URL = "https://www.ooppp.com/soft/update.php";

	public static void Log(string actionCode, string infostr)
	{
		string infostr2 = infostr;
		string actionCode2 = actionCode;
		Task.Run(async delegate
		{
			try
			{
				string value = Uri.EscapeDataString(infostr2);
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(17, 4);
				defaultInterpolatedStringHandler.AppendFormatted("https://www.ooppp.com/soft/update.php");
				defaultInterpolatedStringHandler.AppendLiteral("?a=");
				defaultInterpolatedStringHandler.AppendFormatted(actionCode2);
				defaultInterpolatedStringHandler.AppendLiteral("&i=");
				defaultInterpolatedStringHandler.AppendFormatted(value);
				defaultInterpolatedStringHandler.AppendLiteral("&timestamp=");
				defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now.Ticks);
				string requestUri = defaultInterpolatedStringHandler.ToStringAndClear();
				await _client.GetAsync(requestUri);
			}
			catch
			{
			}
		});
	}
}
