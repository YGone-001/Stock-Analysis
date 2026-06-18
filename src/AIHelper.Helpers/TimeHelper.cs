using System;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace AIHelper.Helpers;

public static class TimeHelper
{
	private static TimeSpan _timeOffset = TimeSpan.Zero;

	public static bool IsSynced { get; private set; } = false;


	public static DateTime BeijingNow
	{
		get
		{
			DateTime dateTime = DateTime.UtcNow;
			if (IsSynced)
			{
				dateTime = dateTime.Add(_timeOffset);
			}
			return dateTime.AddHours(8.0);
		}
	}

	public static async Task SyncTimeAsync()
	{
		try
		{
			using HttpClient client = new HttpClient();
			client.Timeout = TimeSpan.FromSeconds(3.0);
			HttpMethod head = HttpMethod.Head;
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(24, 1);
			defaultInterpolatedStringHandler.AppendLiteral("https://www.baidu.com?t=");
			defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now.Ticks);
			HttpRequestMessage request = new HttpRequestMessage(head, defaultInterpolatedStringHandler.ToStringAndClear());
			HttpResponseMessage httpResponseMessage = await client.SendAsync(request);
			if (httpResponseMessage.Headers.Date.HasValue)
			{
				_timeOffset = httpResponseMessage.Headers.Date.Value.UtcDateTime - DateTime.UtcNow;
				IsSynced = true;
			}
		}
		catch
		{
			IsSynced = false;
		}
	}
}
