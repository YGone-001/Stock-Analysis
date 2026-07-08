using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIHelper.ViewModels;

public partial class MenuItemModel : ObservableObject
{
	[ObservableProperty]
	private string _header;

	[ObservableProperty]
	private string _icon;

	public ICommand Command { get; set; }

	public ObservableCollection<MenuItemModel> Children { get; set; }

	public MenuItemModel()
	{
		Children = new ObservableCollection<MenuItemModel>();
	}
}
