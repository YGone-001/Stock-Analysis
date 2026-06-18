using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIHelper.ViewModels;

public class LogViewModel : INotifyPropertyChanged
{
	private string _logContent;

	public string LogContent
	{
		get
		{
			return _logContent;
		}
		set
		{
			_logContent = value;
			OnPropertyChanged("LogContent");
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	public void Append(string message)
	{
		string value = DateTime.Now.ToString("HH:mm:ss");
		string logContent = LogContent;
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(5, 2);
		defaultInterpolatedStringHandler.AppendLiteral("[");
		defaultInterpolatedStringHandler.AppendFormatted(value);
		defaultInterpolatedStringHandler.AppendLiteral("] ");
		defaultInterpolatedStringHandler.AppendFormatted(message);
		defaultInterpolatedStringHandler.AppendLiteral("\r\n");
		LogContent = logContent + defaultInterpolatedStringHandler.ToStringAndClear();
	}

	public void Clear()
	{
		LogContent = "";
	}

	protected void OnPropertyChanged([CallerMemberName] string name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	public LogViewModel()
	{
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(12, 1);
		defaultInterpolatedStringHandler.AppendLiteral("[");
		defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now, "HH:mm:ss");
		defaultInterpolatedStringHandler.AppendLiteral("] 系统就绪...\r\n");
		_logContent = defaultInterpolatedStringHandler.ToStringAndClear();
		base._002Ector();
	}
}
