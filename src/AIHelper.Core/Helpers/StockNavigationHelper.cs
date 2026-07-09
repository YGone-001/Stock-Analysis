using System;
using System.Text.RegularExpressions;

namespace AIHelper.Helpers;

public static class StockNavigationHelper
{
	public static string GetCode(object source)
	{
		if (source == null) return "";
		if (source is string text) return NormalizeCode(text);
		if (source is AIHelper.Models.StockModel stockModel)
		{
			string code = stockModel.Code;
			if (string.IsNullOrWhiteSpace(code)) code = stockModel.PureCode;
			return NormalizeCode(code);
		}
		return "";
	}

	public static string GetName(object source)
	{
		if (source == null) return "未知股票";
		if (source is AIHelper.Models.StockModel stockModel)
		{
			return string.IsNullOrWhiteSpace(stockModel.Name) ? "未知股票" : stockModel.Name;
		}
		return "未知股票";
	}

	private static readonly Regex CodeRegex = new Regex("\\d{6}", RegexOptions.Compiled);

	public static string NormalizeCode(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return "";
		}
		Match match = CodeRegex.Match(code);
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
