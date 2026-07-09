using System;

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AIHelper.Models;
using AIHelper.ViewModels;

namespace AIHelper.Views;

public partial class StockView : UserControl
{

	public StockView()
	{
		InitializeComponent();
	}

	private void BtnMove_Click(object sender, RoutedEventArgs e)
	{
		Button button = sender as Button;
		StockModel stockModel = button?.DataContext as StockModel;
		StockViewModel vm = base.DataContext as StockViewModel;
		if (stockModel == null || vm == null || vm.StockGroups == null)
		{
			return;
		}
		vm.CurrentSelectedStock = stockModel;
		ContextMenu contextMenu = new ContextMenu();
		foreach (StockGroupModel stockGroup in vm.StockGroups)
		{
			if (stockGroup.IsOverview)
			{
				continue;
			}
			MenuItem menuItem = new MenuItem
			{
				Header = stockGroup.Header
			};
			StockGroupModel targetGroup = stockGroup;
			menuItem.Click += delegate
			{
				if (vm.MoveStockToGroupCommand.CanExecute(targetGroup))
				{
					vm.MoveStockToGroupCommand.Execute(targetGroup);
				}
			};
			contextMenu.Items.Add(menuItem);
		}
		if (contextMenu.Items.Count == 0)
		{
			contextMenu.Items.Add(new MenuItem
			{
				Header = "暂无其他分组",
				IsEnabled = false
			});
		}
		contextMenu.PlacementTarget = button;
		contextMenu.IsOpen = true;
	}

}
