import os
import re

filepath = 'e:/AIHelper-dev/src/AIHelper.Helpers/NetworkHelper.cs'
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

get_web_proxy = """	public static WebProxy GetWebProxy(AppConfig config)
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

"""

if 'public static WebProxy GetWebProxy' not in content:
    content = content.replace('\tpublic static void ReloadProxySettings()', get_web_proxy + '\tpublic static void ReloadProxySettings()')

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)
