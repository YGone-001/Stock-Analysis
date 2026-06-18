using System;
using System.IO;

namespace AIHelper.Models;

public class AppConfig
{
	public int TimeoutSeconds { get; set; } = 60;


	public double WindowTop { get; set; } = 100.0;


	public double WindowLeft { get; set; } = 100.0;


	public double WindowWidth { get; set; } = 1355.0;


	public double WindowHeight { get; set; } = 700.0;


	public bool IsMaximized { get; set; }

	public double RightColumnWidth { get; set; } = 300.0;


	public double LogRowHeight { get; set; } = 150.0;


	public string DataSavePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");


	public bool IsAutoOpenFolder { get; set; }

	public bool IsChatVisible { get; set; } = true;


	public bool IsJsonMinify { get; set; } = true;


	public bool IsShowIndex { get; set; } = true;


	public bool IsShowSH { get; set; } = true;


	public bool IsShowSZ { get; set; }

	public bool IsShowCY { get; set; }

	public bool IsShowHS300 { get; set; }

	public int IndexKLineCount { get; set; } = 5;


	public int StockKLineCount { get; set; } = 30;


	public bool IsQueryTick { get; set; }

	public bool IsQueryKLine { get; set; } = true;


	public string AkServerUrl { get; set; } = "https://www.98da.com";


	public bool ExportIsSingleFile { get; set; } = true;


	public bool ExportIncludeHoldingPrompt { get; set; } = true;


	public int ExportKlineDays { get; set; } = 60;


	public int ExportIndexDays { get; set; } = 5;


	public bool ExportIdxSH { get; set; }

	public bool ExportIdxSZ { get; set; }

	public bool ExportIdxCY { get; set; }

	public bool ExportIdxHS300 { get; set; }

	public bool ExportComboQuote { get; set; } = true;


	public bool ExportComboMinute { get; set; } = true;


	public bool ExportComboKline { get; set; } = true;


	public bool ExportComboTick { get; set; } = true;


	public bool IsProxyEnabled { get; set; }

	public string ProxyAddress { get; set; } = "127.0.0.1";


	public int ProxyPort { get; set; } = 7890;


	public string ProxyUserName { get; set; } = "";


	public string ProxyPassword { get; set; } = "";


	public string SavedChatUser { get; set; } = "";


	public string SavedChatPwd { get; set; } = "";


	public bool IsChatAutoLogin { get; set; }
}
