using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AIHelper.Models;
using AIHelper.ViewModels;

namespace AIHelper.Views;

public class StockView : UserControl, IComponentConnector, IStyleConnector
{
	internal TextBox TxtNewGroup;

	internal TextBox TxtSearch;

	private bool _contentLoaded;

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

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/stockview.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			TxtNewGroup = (TextBox)target;
			break;
		case 2:
			TxtSearch = (TextBox)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IStyleConnector.Connect(int connectionId, object target)
	{
		if (connectionId == 3)
		{
			((Button)target).Click += BtnMove_Click;
		}
	}
}
