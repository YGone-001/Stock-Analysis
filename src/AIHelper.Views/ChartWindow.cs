using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using HandyControl.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIHelper.Views;

public class ChartWindow : System.Windows.Window, IComponentConnector
{
	private string _url;

	internal WebView2 MyWebView;

	internal Border LoadingMask;

	private bool _contentLoaded;

	public ChartWindow(string url, string title)
	{
		InitializeComponent();
		_url = url;
		base.Title = title;
		InitializeAsync();
	}

	private async void InitializeAsync()
	{
		try
		{
			string userDataFolder = Path.Combine(Path.GetTempPath(), "AIHelper_WebView2_Cache");
			CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
			await MyWebView.EnsureCoreWebView2Async(environment);
			await MyWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("document.documentElement.style.opacity = '0';");
			MyWebView.WebMessageReceived += MyWebView_WebMessageReceived;
			MyWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
			MyWebView.Source = new Uri(_url);
		}
		catch (Exception ex2)
		{
			Exception ex = ex2;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				HandyControl.Controls.MessageBox.Show("图表引擎加载失败！\n\n可能原因：\n1. 您的电脑未安装 WebView2 运行时。\n2. 杀毒软件拦截了组件。\n\n系统报错：" + ex.Message, "环境缺失拦截", MessageBoxButton.OK, MessageBoxImage.Hand);
				Close();
			});
		}
	}

	private async void MyWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		if (e.TryGetWebMessageAsString() == "crop_done")
		{
			await Task.Delay(500);
			Application.Current?.Dispatcher.Invoke(delegate
			{
				LoadingMask.Visibility = Visibility.Collapsed;
				MyWebView.Visibility = Visibility.Visible;
			});
		}
	}

	private async void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
	{
		if (e.IsSuccess)
		{
			if (_url.Contains("eastmoney.com", StringComparison.OrdinalIgnoreCase))
			{
				LoadingMask.Visibility = Visibility.Collapsed;
				MyWebView.Visibility = Visibility.Visible;
				return;
			}
			string javaScript = "\r\n                    let checkTimes = 0;\r\n                    let findTimer = setInterval(function() {\r\n                        checkTimes++;\r\n                        \r\n                        // ?? 核心修改：将问财的靶心从 .kline2_outer 切换为更紧凑的 .jgy_kline2_page\r\n                        let targetNode = document.querySelector('.jgy_kline2_page') || document.querySelector('.wrap.clearfix');\r\n                        \r\n                        if (targetNode) {\r\n                            clearInterval(findTimer);\r\n                            \r\n                            let current = targetNode;\r\n                            while (current && current !== document.body) {\r\n                                let siblings = current.parentElement.children;\r\n                                for (let i = 0; i < siblings.length; i++) {\r\n                                    let sibling = siblings[i];\r\n                                    if (sibling !== current && sibling.tagName !== 'STYLE' && sibling.tagName !== 'SCRIPT' && sibling.tagName !== 'LINK') {\r\n                                        sibling.style.display = 'none';\r\n                                    }\r\n                                }\r\n                                current.parentElement.style.padding = '0';\r\n                                current.parentElement.style.margin = '0';\r\n                                current = current.parentElement;\r\n                            }\r\n                            \r\n                            document.body.style.overflow = 'auto';\r\n                            document.body.style.backgroundColor = '#ffffff';\r\n                            \r\n                            targetNode.style.margin = '0';\r\n                            targetNode.style.padding = '5px'; // ?? 把 padding 收紧，原来是 10px，现在改成 5px，更清爽\r\n                            targetNode.style.width = '100vw'; \r\n                            targetNode.style.boxSizing = 'border-box';\r\n                            targetNode.style.backgroundColor = '#ffffff';\r\n\r\n                            window.dispatchEvent(new Event('resize'));\r\n                            setTimeout(() => window.dispatchEvent(new Event('resize')), 500); \r\n\r\n                            // 呼叫 WPF 总部：切完了，可以放我出去了！\r\n                            window.chrome.webview.postMessage('crop_done');\r\n                        }\r\n                        else if (checkTimes > 50) {\r\n                            clearInterval(findTimer);\r\n                            window.chrome.webview.postMessage('crop_done');\r\n                        }\r\n                    }, 200);\r\n                ";
			await MyWebView.CoreWebView2.ExecuteScriptAsync(javaScript);
		}
		else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
		{
			LoadingMask.Visibility = Visibility.Collapsed;
			MyWebView.Visibility = Visibility.Visible;
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		base.OnClosed(e);
		try
		{
			MyWebView?.Dispose();
		}
		catch (Exception ex)
		{
			Trace.WriteLine("Failed to dispose WebView2: " + ex.Message);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/chartwindow.xaml", UriKind.Relative);
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
			MyWebView = (WebView2)target;
			break;
		case 2:
			LoadingMask = (Border)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
