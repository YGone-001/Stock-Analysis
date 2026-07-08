#nullable enable
using System;
using System.CodeDom.Compiler;
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

public class LiveChartWindow : Window, IComponentConnector
{
	private string _code = string.Empty;

	private string _name = string.Empty;

	internal TextBlock TxtInfo = null!;

	internal ProgressBar LoadProgress = null!;

	internal WebView2 WebView = null!;

	private bool _contentLoaded;

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
		catch (Exception ex)
		{
			MessageBox.Show("浏览器引擎初始化失败: " + ex.Message);
		}
	}

	private void BtnRefresh_Click(object sender, RoutedEventArgs e)
	{
		WebView.Reload();
		LoadProgress.Visibility = Visibility.Visible;
	}

	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/livechartwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			TxtInfo = (TextBlock)target;
			break;
		case 2:
			LoadProgress = (ProgressBar)target;
			break;
		case 3:
			((Button)target).Click += BtnRefresh_Click;
			break;
		case 4:
			WebView = (WebView2)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
