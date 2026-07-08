#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIHelper.ViewModels;

public class LogViewModel : INotifyPropertyChanged
{
	private string _logContent = string.Empty;

	public string LogContent
	{
		get => _logContent;
		set
		{
			_logContent = value;
			OnPropertyChanged(nameof(LogContent));
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged = null;

	public void Append(string message)
	{
		string text = $"[{DateTime.Now:HH:mm:ss}] {message}\r\n";
		string newLog = _logContent + text;
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

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	public LogViewModel()
	{
		_logContent = $"[{DateTime.Now:HH:mm:ss}] 系统就绪...\r\n";
	}
}
