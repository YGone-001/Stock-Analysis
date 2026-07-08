using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AIHelper.ViewModels;

public class MenuItemModel : INotifyPropertyChanged
{
	private string _header;
	private string _icon;

	public string Header
	{
		get => _header;
		set
		{
			if (_header != value)
			{
				_header = value;
				OnPropertyChanged(nameof(Header));
			}
		}
	}

	public string Icon
	{
		get => _icon;
		set
		{
			if (_icon != value)
			{
				_icon = value;
				OnPropertyChanged(nameof(Icon));
			}
		}
	}

	public ICommand Command { get; set; }

	public ObservableCollection<MenuItemModel> Children { get; set; }

	public MenuItemModel()
	{
		Children = new ObservableCollection<MenuItemModel>();
	}

	public event PropertyChangedEventHandler PropertyChanged;

	protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
