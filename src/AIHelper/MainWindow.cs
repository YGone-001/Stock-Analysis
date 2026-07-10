using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.ViewModels;
using AIHelper.Views;
using HandyControl.Controls;

namespace AIHelper;

public class MainWindow : HandyControl.Controls.Window, IComponentConnector
{
	internal RowDefinition RowLog;

	internal ExportControl ExportCtrl;

	internal ChatView MyChatView;

	private bool _contentLoaded;

	public MainWindow(MainViewModel vm)
	{
		InitializeComponent();
		base.DataContext = vm;
		ExportCtrl.PrintLogAction = delegate(string message)
		{
			vm.AppendLog(message);
		};
		ExportCtrl.GetSelectedStocksFunc = () => vm.StockVM.GetSelectedStocks();
		ExportCtrl.GetCurrentTabNameFunc = () => vm.StockVM.CurrentGroupName ?? "默认分组";


		base.Loaded += MainWindow_Loaded;
		UpdateHelper.CheckUpdateAsync().SafeFireAndForget();
	}

	private void MainWindow_Loaded(object sender, RoutedEventArgs e)
	{
		AppConfig appConfig = ConfigManager.Load();
		base.Width = appConfig.WindowWidth;
		base.Height = appConfig.WindowHeight;
		base.Top = appConfig.WindowTop;
		base.Left = appConfig.WindowLeft;
		if (appConfig.IsMaximized)
		{
			base.WindowState = WindowState.Maximized;
		}
		if (base.DataContext is MainViewModel mainViewModel)
		{
			if (appConfig.RightColumnWidth > 0.0)
			{
				mainViewModel.ChatColumnWidth = new GridLength(appConfig.RightColumnWidth);
			}
			if (!appConfig.IsChatVisible)
			{
				mainViewModel.ChatVisibility = Visibility.Collapsed;
				mainViewModel.ChatColumnWidth = new GridLength(0.0);
			}
			mainViewModel.IsAutoOpen = appConfig.IsAutoOpenFolder;
		}
		if (appConfig.LogRowHeight > 0.0)
		{
			RowLog.Height = new GridLength(appConfig.LogRowHeight, GridUnitType.Pixel);
		}
		ExportCtrl.ApplyConfig(appConfig);
	}

	private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox textBox)
		{
			textBox.ScrollToEnd();
		}
	}

	private void Window_Closing(object? sender, CancelEventArgs e)
	{
		AppConfig appConfig = ConfigManager.Load();
		if (base.WindowState == WindowState.Maximized)
		{
			appConfig.IsMaximized = true;
			appConfig.WindowWidth = base.RestoreBounds.Width;
			appConfig.WindowHeight = base.RestoreBounds.Height;
			appConfig.WindowTop = base.RestoreBounds.Top;
			appConfig.WindowLeft = base.RestoreBounds.Left;
		}
		else
		{
			appConfig.IsMaximized = false;
			appConfig.WindowWidth = base.Width;
			appConfig.WindowHeight = base.Height;
			appConfig.WindowTop = base.Top;
			appConfig.WindowLeft = base.Left;
		}
		if (base.DataContext is MainViewModel mainViewModel)
		{
			appConfig.IsChatVisible = mainViewModel.ChatVisibility == Visibility.Visible;
			if (mainViewModel.ChatVisibility == Visibility.Visible && mainViewModel.ChatColumnWidth.Value > 10.0)
			{
				appConfig.RightColumnWidth = mainViewModel.ChatColumnWidth.Value;
			}
			appConfig.IsAutoOpenFolder = mainViewModel.IsAutoOpen;
		}
		appConfig.LogRowHeight = RowLog.ActualHeight;
		ExportCtrl.SyncToConfig(appConfig);
		ConfigManager.Save(appConfig);
		if (base.DataContext is MainViewModel mainViewModel2)
		{
			mainViewModel2.StockVM.SaveLocalData();
			mainViewModel2.Dispose();
		}
	}

	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/mainwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	internal Delegate _CreateDelegate(Type delegateType, string handler)
	{
		return Delegate.CreateDelegate(delegateType, this, handler);
	}

	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((MainWindow)target).Closing += Window_Closing;
			break;
		case 2:
			RowLog = (RowDefinition)target;
			break;
		case 3:
			ExportCtrl = (ExportControl)target;
			break;
		case 4:
			((System.Windows.Controls.TextBox)target).TextChanged += TextBox_TextChanged;
			break;
		case 5:
			MyChatView = (ChatView)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
