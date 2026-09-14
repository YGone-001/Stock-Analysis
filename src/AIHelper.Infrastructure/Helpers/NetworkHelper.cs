using System;
using System.Net;
using System.Net.NetworkInformation;
using AIHelper.Models;

#pragma warning disable CS8600, CS8603, CS8604, CS8618, CS8625
#pragma warning disable CS8600, CS8603, CS8604, CS8618, CS8625
namespace AIHelper.Helpers;

public static class NetworkHelper
{
	public static WebProxy GetWebProxy(AppConfig config)
	{
		if (!config.IsProxyEnabled || string.IsNullOrWhiteSpace(config.ProxyAddress)) return null;
		string address = config.ProxyAddress.Trim();
		if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
		{
			address = "http://" + address;
		}
		string portStr = string.IsNullOrWhiteSpace(config.ProxyPort) ? "" : ":" + config.ProxyPort.Trim();
		WebProxy proxy = new WebProxy(new Uri(address + portStr));
		if (!string.IsNullOrEmpty(config.ProxyUserName))
		{
			proxy.Credentials = new NetworkCredential(config.ProxyUserName, config.ProxyPassword);
		}
		return proxy;
	}

	public static void ReloadProxySettings()
	{
		// With DI, HttpMessageHandler configuration is handled in App.cs
	}


	public static bool PingServer(string host)
	{
		try
		{
			using Ping ping = new Ping();
			return ping.Send(host, 1000).Status == IPStatus.Success;
		}
		catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			return false;
		}
	}

}
