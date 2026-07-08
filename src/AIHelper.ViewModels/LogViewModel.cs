#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIHelper.ViewModels;

public partial class LogViewModel : ObservableObject
{
	[ObservableProperty]
	private string _logContent = string.Empty;

	public void Append(string message)
	{
		string text = $"[{DateTime.Now:HH:mm:ss}] {message}\r\n";
		string newLog = LogContent + text;
		if (newLog.Length > 20000)
		{
			int startIndex = newLog.IndexOf('\n', newLog.Length - 10000) + 1;
			if (startIndex <= 0) startIndex = newLog.Length - 10000;
			newLog = newLog.Substring(startIndex);
		}
		LogContent = newLog;
	}

	public void Clear()
	{
		LogContent = "";
	}

	public LogViewModel()
	{
		LogContent = $"[{DateTime.Now:HH:mm:ss}] 系统就绪...\r\n";
	}
}
