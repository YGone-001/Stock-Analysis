using System;
using System.Windows;
using AIHelper.ViewModels;
using AIHelper.Views;
using HandyControl.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace AIHelper.Services;

public class WpfDialogService : IDialogService
{
	private readonly IServiceProvider _serviceProvider;

	public WpfDialogService(IServiceProvider serviceProvider)
	{
		_serviceProvider = serviceProvider;
	}

	public void ShowMessage(string message, string title)
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			HandyControl.Controls.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
		});
	}

	public bool ShowConfirm(string message, string title)
	{
		return Application.Current?.Dispatcher.Invoke(() =>
		{
			return HandyControl.Controls.MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes;
		}) ?? false;
	}

	public string ShowInput(string title, string defaultValue = "")
	{
		return Application.Current?.Dispatcher.Invoke(() =>
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
			System.Windows.Controls.StackPanel stackPanel = new System.Windows.Controls.StackPanel { Margin = new Thickness(15.0) };
			System.Windows.Controls.TextBox tb = new System.Windows.Controls.TextBox
			{
				Text = defaultValue, FontSize = 14.0, Padding = new Thickness(5.0), Margin = new Thickness(0.0, 0.0, 0.0, 15.0)
			};
			System.Windows.Controls.Button button = new System.Windows.Controls.Button { Content = "确定", Width = 80.0, Height = 30.0, IsDefault = true, Cursor = System.Windows.Input.Cursors.Hand };
			button.Click += delegate
			{
				result = tb.Text;
				inputWin.Close();
			};
			stackPanel.Children.Add(new System.Windows.Controls.TextBlock { Text = "请输入名称：", Margin = new Thickness(0.0, 0.0, 0.0, 5.0), FontWeight = FontWeights.Bold });
			stackPanel.Children.Add(tb);
			stackPanel.Children.Add(button);
			inputWin.Content = stackPanel;
			tb.SelectAll();
			tb.Focus();
			inputWin.ShowDialog();
			return result;
		});
	}

	public void ShowChart(string url, string title)
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			new ChartWindow(url, title).Show();
		});
	}

	public void ShowImage(string imageUrl)
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			ImageBrowser imageBrowser = new ImageBrowser(new Uri(imageUrl))
			{
				Owner = Application.Current.MainWindow
			};
			imageBrowser.Show();
		});
	}

	public void ShowWenCai(string code, string name)
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			WenCaiWindow wenCaiWindow = new WenCaiWindow(code, name)
			{
				Owner = Application.Current.MainWindow
			};
			wenCaiWindow.Show();
		});
	}

	public void ShowLiveChart(string code, string name)
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			LiveChartWindow liveChartWindow = new LiveChartWindow(code, name)
			{
				Owner = Application.Current.MainWindow
			};
			liveChartWindow.Show();
		});
	}

	public void ShowPositionConfig(string code, string name)
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			PositionWindow positionWindow = new PositionWindow(code, name)
			{
				Owner = Application.Current.MainWindow
			};
			positionWindow.ShowDialog();
		});
	}

	public void ShowImportExport()
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			// Need MainViewModel for ImportExportWindow
			var mainVm = _serviceProvider.GetRequiredService<MainViewModel>();
			ImportExportWindow importExportWindow = new ImportExportWindow(mainVm)
			{
				Owner = Application.Current.MainWindow
			};
			importExportWindow.ShowDialog();
		});
	}

	public void ShowProxySettings()
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			new ProxyWindow
			{
				Owner = Application.Current.MainWindow
			}.ShowDialog();
		});
	}

	public void ShowSparrowScanner()
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			var mainVm = _serviceProvider.GetRequiredService<MainViewModel>();
			new SparrowWindowDC(mainVm)
			{
				Owner = Application.Current.MainWindow
			}.Show();
		});
	}
}
