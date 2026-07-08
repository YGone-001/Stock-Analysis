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
using HandyControl.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIHelper.Views;

public class WenCaiWindow : System.Windows.Window, IComponentConnector
{
	private string _stockCode = string.Empty;

	internal TextBlock TxtStatus = null!;

	internal WebView2 WenCaiWebView = null!;

	private bool _contentLoaded;

	public WenCaiWindow(string stockCode, string stockName)
	{
		InitializeComponent();
		_stockCode = StockNavigationHelper.NormalizeCode(stockCode);
		base.Title = $"问财智能分析 - {stockName} ({_stockCode})";
		base.Loaded += WenCaiWindow_Loaded;
		base.Closed += delegate
		{
			if (WenCaiWebView != null)
			{
				WenCaiWebView.Dispose();
			}
		};
	}

	private async void WenCaiWindow_Loaded(object sender, RoutedEventArgs e)
	{
		try
		{
			TxtStatus.Text = "⏳ 正在初始化本地浏览器引擎...";
			await WenCaiWebView.EnsureCoreWebView2Async(null);
			string pureCode = StockNavigationHelper.NormalizeCode(_stockCode);
			TxtStatus.Text = "\ud83c\udf10 正在请求问财数据：" + pureCode;
			string uri = "https://www.iwencai.com/unifiedwap/result?w=" + pureCode;
			WenCaiWebView.CoreWebView2.Navigate(uri);
			WenCaiWebView.CoreWebView2.NavigationCompleted += delegate(object? s, CoreWebView2NavigationCompletedEventArgs args)
			{
				if (args.IsSuccess)
				{
					TxtStatus.Text = "✅ 页面加载完成 - 问财搜索：" + pureCode;
				}
				else
				{
					TxtStatus.Text = "❌ 页面加载失败，请检查网络！";
				}
			};
		}
		catch (Exception ex)
		{
			TxtStatus.Text = "❌ 浏览器初始化失败: " + ex.Message;
			Growl.Error("无法启动内置浏览器，请检查 WebView2 运行时是否安装！");
		}
	}

	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/wencaiwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			TxtStatus = (TextBlock)target;
			break;
		case 2:
			WenCaiWebView = (WebView2)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
