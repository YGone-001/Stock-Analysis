#nullable enable
using System;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AIHelper.Helpers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIHelper.Views;

public partial class LiveChartWindow : Window
{
	private string _code = string.Empty;

	private string _name = string.Empty;

	public LiveChartWindow(string code, string name)
	{
		InitializeComponent();
		_code = StockNavigationHelper.NormalizeCode(code);
		_name = name;
		TxtInfo.Text = name + " (" + _code + ") - 东方财富行情";
		base.Closed += delegate
		{
			WebView?.Dispose();
		};
		InitializeWebView();
	}

	private async void InitializeWebView()
	{
		try
		{
			await WebView.EnsureCoreWebView2Async(null);
			string uri = StockNavigationHelper.BuildEastMoneyQuoteUrl(_code);
			WebView.CoreWebView2.Navigate(uri);
			WebView.NavigationCompleted += delegate(object? s, CoreWebView2NavigationCompletedEventArgs e)
			{
				LoadProgress.Visibility = Visibility.Collapsed;
				if (!e.IsSuccess)
				{
					TextBlock txtInfo = TxtInfo;
					txtInfo.Text = $"❌ 状态码: {e.WebErrorStatus}，页面加载失败。";
				}
			};
		}
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			MessageBox.Show("浏览器引擎初始化失败: " + ex.Message);
		}
	}

	private void BtnRefresh_Click(object sender, RoutedEventArgs e)
	{
		WebView.Reload();
		LoadProgress.Visibility = Visibility.Visible;
	}

}
