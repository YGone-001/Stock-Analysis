using System;
using System.Text.RegularExpressions;

namespace AIHelper.Helpers;

public static class StockNavigationHelper
{
	public static string GetCode(object source)
	{
		if (source == null)
		{
			return "";
		}
		if (source is string text)
		{
			return NormalizeCode(text);
		}
		string code = source.GetType().GetProperty("Code")?.GetValue(source)?.ToString() ?? "";
		if (string.IsNullOrWhiteSpace(code))
		{
			code = source.GetType().GetProperty("PureCode")?.GetValue(source)?.ToString() ?? "";
		}
		return NormalizeCode(code);
	}

	public static string GetName(object source)
	{
		if (source == null)
		{
			return "未知股票";
		}
		string name = source.GetType().GetProperty("Name")?.GetValue(source)?.ToString() ?? "";
		return string.IsNullOrWhiteSpace(name) ? "未知股票" : name;
	}

	public static string NormalizeCode(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return "";
		}
		Match match = Regex.Match(code, "\\d{6}");
		return match.Success ? match.Value : code.Trim();
	}

	public static string GetEastMoneyPrefix(string code)
	{
		code = NormalizeCode(code);
		if (code.StartsWith("6") || code.StartsWith("5") || code.StartsWith("9"))
		{
			return "sh";
		}
		if (code.StartsWith("4") || code.StartsWith("8"))
		{
			return "bj";
		}
		return "sz";
	}

	public static string GetEastMoneyMarketId(string code)
	{
		return GetEastMoneyPrefix(code) == "sh" ? "1" : "0";
	}

	public static string BuildEastMoneyQuoteUrl(string code, bool fullScreenChart = false)
	{
		string pureCode = NormalizeCode(code);
		string url = "https://quote.eastmoney.com/" + GetEastMoneyPrefix(pureCode) + pureCode + ".html";
		return fullScreenChart ? url + "#fullScreenChart" : url;
	}

	public static string BuildEastMoneyFiveDayImageUrl(string code)
	{
		string pureCode = NormalizeCode(code);
		string marketId = GetEastMoneyMarketId(pureCode);
		return "https://webquotepic.eastmoney.com/GetPic.aspx?imageType=t&type=M4&nid=" + marketId + "." + pureCode + "&timespan=" + DateTimeOffset.Now.ToUnixTimeSeconds();
	}
}
