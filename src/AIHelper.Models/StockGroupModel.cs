using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AIHelper.Models;

public class StockGroupModel : INotifyPropertyChanged
{
	private string _header = "新建分组";

	public string GroupId { get; set; } = Guid.NewGuid().ToString();


	public string Header
	{
		get
		{
			return _header;
		}
		set
		{
			if (_header != value)
			{
				_header = value;
				OnPropertyChanged("Header");
			}
		}
	}

	[JsonIgnore]
	public bool IsOverview { get; set; }

	public ObservableCollection<StockModel> Stocks { get; set; } = new ObservableCollection<StockModel>();


	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
