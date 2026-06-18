using System;
using System.Collections.Generic;

namespace AIHelper.Services;

public class ExportConfig
{
	public List<(string Code, string Name)> SelectedStocks { get; set; } = new List<(string, string)>();


	public DateTime TargetDate { get; set; }

	public bool FetchQuote { get; set; }

	public bool FetchMinute { get; set; }

	public bool FetchTick { get; set; }

	public bool FetchKline { get; set; }

	public int KlineDays { get; set; } = 100;


	public int IndexDays { get; set; } = 5;


	public List<string> SelectedIndices { get; set; } = new List<string>();


	public bool EnableAiCompression { get; set; }

	public bool IncludeHoldingPrompt { get; set; } = true;


	public bool IsSingleFileMode { get; set; }

	public string TabName { get; set; } = "默认分组";

}
