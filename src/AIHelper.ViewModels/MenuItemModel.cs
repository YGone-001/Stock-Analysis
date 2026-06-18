using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AIHelper.ViewModels;

public class MenuItemModel
{
	public string Header { get; set; }

	public string Icon { get; set; }

	public ICommand Command { get; set; }

	public ObservableCollection<MenuItemModel> Children { get; set; }

	public MenuItemModel()
	{
		Children = new ObservableCollection<MenuItemModel>();
	}
}
