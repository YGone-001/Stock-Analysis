using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

#pragma warning disable CS8618
#pragma warning disable CS8618
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
