#nullable enable
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AIHelper.Models;

public class StockModel : INotifyPropertyChanged
{
	private string _code = "";

	private bool _isChecked;

	private string _name = "--";

	private double _price;

	private double _percent;

	private double _lastClose;

	private double _open;

	private double _high;

	private double _low;

	private double _turnover;

	private int _highlightLevel;

	public string Code
	{
		get
		{
			return _code;
		}
		set
		{
			_code = value;
			OnPropertyChanged(nameof(Code));
		}
	}

	[JsonIgnore]
	public string PureCode
	{
		get
		{
			if (string.IsNullOrEmpty(Code))
			{
				return "";
			}
			return Code.ToLower().Replace("sh", "").Replace("sz", "")
				.Replace("bj", "");
		}
	}

	public bool IsChecked
	{
		get
		{
			return _isChecked;
		}
		set
		{
			_isChecked = value;
			OnPropertyChanged(nameof(IsChecked));
		}
	}

	[JsonIgnore]
	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			_name = value;
			OnPropertyChanged(nameof(Name));
		}
	}

	[JsonIgnore]
	public double Price
	{
		get
		{
			return _price;
		}
		set
		{
			_price = value;
			OnPropertyChanged(nameof(Price));
			OnPropertyChanged(nameof(PriceColor));
		}
	}

	[JsonIgnore]
	public double Percent
	{
		get
		{
			return _percent;
		}
		set
		{
			_percent = value;
			OnPropertyChanged(nameof(Percent));
			OnPropertyChanged(nameof(PercentText));
			OnPropertyChanged(nameof(PercentBgColor));
		}
	}

	[JsonIgnore]
	public double LastClose
	{
		get
		{
			return _lastClose;
		}
		set
		{
			_lastClose = value;
			OnPropertyChanged(nameof(LastClose));
		}
	}

	[JsonIgnore]
	public double Open
	{
		get
		{
			return _open;
		}
		set
		{
			_open = value;
			OnPropertyChanged(nameof(Open));
		}
	}

	[JsonIgnore]
	public double High
	{
		get
		{
			return _high;
		}
		set
		{
			_high = value;
			OnPropertyChanged(nameof(High));
		}
	}

	[JsonIgnore]
	public double Low
	{
		get
		{
			return _low;
		}
		set
		{
			_low = value;
			OnPropertyChanged(nameof(Low));
		}
	}

	[JsonIgnore]
	public double Turnover
	{
		get
		{
			return _turnover;
		}
		set
		{
			_turnover = value;
			OnPropertyChanged(nameof(Turnover));
		}
	}

	[JsonIgnore]
	public string PercentText => ((Percent > 0.0) ? "+" : "") + Percent.ToString("F2") + "%";

	public static bool IsRedUp { get; set; } = true;


	[JsonIgnore]
	public string PriceColor
	{
		get
		{
			if (Percent == 0.0)
			{
				return "Gray";
			}
			if (IsRedUp)
			{
				if (!(Percent > 0.0))
				{
					return "#00CC00";
				}
				return "#FF3333";
			}
			if (!(Percent > 0.0))
			{
				return "#FF3333";
			}
			return "#00CC00";
		}
	}

	[JsonIgnore]
	public string PercentBgColor
	{
		get
		{
			if (Percent == 0.0)
			{
				return "Transparent";
			}
			if (IsRedUp)
			{
				if (!(Percent > 0.0))
				{
					return "#33008000";
				}
				return "#33FF0000";
			}
			if (!(Percent > 0.0))
			{
				return "#33FF0000";
			}
			return "#33008000";
		}
	}

	public int HighlightLevel
	{
		get
		{
			return _highlightLevel;
		}
		set
		{
			_highlightLevel = value;
			OnPropertyChanged(nameof(HighlightLevel));
			OnPropertyChanged(nameof(CodeColor));
		}
	}

	[JsonIgnore]
	public string CodeColor => HighlightLevel switch
	{
		2 => "#DAA520", 
		1 => "#FF4500", 
		_ => "#666666", 
	};

	public event PropertyChangedEventHandler? PropertyChanged = null;

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
