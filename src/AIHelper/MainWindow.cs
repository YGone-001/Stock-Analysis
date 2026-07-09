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
		
		vm.OpenProxyWindowAction = () => new ProxyWindow { Owner = this }.ShowDialog();
		vm.OpenImportExportWindowAction = () => new ImportExportWindow(vm) { Owner = this }.ShowDialog();
		vm.OpenPositionWindowAction = (code, name) => new PositionWindow(code, name) { Owner = this }.ShowDialog();
		vm.OpenSparrowWindowAction = () => new SparrowWindowDC(vm) { Owner = this }.Show();
		vm.ShowConfirmFunc = (msg, title) => HandyControl.Controls.MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes;
		
		vm.StockVM.ShowInputDialogFunc = (title, defaultValue) =>
		{
			string result = defaultValue;
			System.Windows.Window inputWin = new System.Windows.Window
			{
				Title = title,
				Width = 300.0,
				Height = 180.0,
				WindowStartupLocation = WindowStartupLocation.CenterScreen,
				ResizeMode = ResizeMode.NoResize,
				Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F3F4F6"))
			};
			StackPanel stackPanel = new StackPanel { Margin = new Thickness(15.0) };
			System.Windows.Controls.TextBox tb = new System.Windows.Controls.TextBox
			{
				Text = defaultValue, FontSize = 14.0, Padding = new Thickness(5.0), Margin = new Thickness(0.0, 0.0, 0.0, 15.0)
			};
			Button button = new Button { Content = "确定", Width = 80.0, Height = 30.0, IsDefault = true, Cursor = System.Windows.Input.Cursors.Hand };
			button.Click += delegate
			{
				result = tb.Text;
				inputWin.Close();
			};
			stackPanel.Children.Add(new TextBlock { Text = "请输入名称：", Margin = new Thickness(0.0, 0.0, 0.0, 5.0), FontWeight = FontWeights.Bold });
			stackPanel.Children.Add(tb);
			stackPanel.Children.Add(button);
			inputWin.Content = stackPanel;
			tb.SelectAll();
			tb.Focus();
			inputWin.ShowDialog();
			return result;
		};

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
