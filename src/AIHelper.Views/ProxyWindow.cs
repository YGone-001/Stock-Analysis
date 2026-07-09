using System;

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using AIHelper.Helpers;
using AIHelper.Models;
using HandyControl.Controls;

namespace AIHelper.Views;

public partial class ProxyWindow : HandyControl.Controls.Window
{

	public ProxyWindow()
	{
		InitializeComponent();
		base.Loaded += ProxyWindow_Loaded;
	}

	private void ProxyWindow_Loaded(object sender, RoutedEventArgs e)
	{
		AppConfig appConfig = ConfigManager.Load();
		TglEnableProxy.IsChecked = appConfig.IsProxyEnabled;
		TxtAddress.Text = appConfig.ProxyAddress;
		TxtPort.Text = appConfig.ProxyPort.ToString();
		TxtUsername.Text = appConfig.ProxyUserName;
		TxtPassword.Password = appConfig.ProxyPassword;
	}

	private async void BtnTest_Click(object sender, RoutedEventArgs e)
	{
		if (TglEnableProxy.IsChecked.GetValueOrDefault() && (string.IsNullOrWhiteSpace(TxtAddress.Text) || !int.TryParse(TxtPort.Text, out var _)))
		{
			HandyControl.Controls.MessageBox.Show("请填写正确的代理地址和数字端口号！", "格式错误", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		BtnTest.IsEnabled = false;
		TxtTestResult.Text = "正在测试连接...";
		TxtTestResult.Foreground = new SolidColorBrush(Colors.Gray);
		try
		{
			HttpClientHandler httpClientHandler = new HttpClientHandler
			{
				UseProxy = false
			};
			if (TglEnableProxy.IsChecked.GetValueOrDefault())
			{
				string text = TxtAddress.Text.Trim();
				if (!text.StartsWith("http") && !text.StartsWith("socks5"))
				{
					text = "http://" + text;
				}
				WebProxy webProxy = new WebProxy(new Uri(text + ":" + TxtPort.Text.Trim()));
				if (!string.IsNullOrWhiteSpace(TxtUsername.Text))
				{
					webProxy.Credentials = new NetworkCredential(TxtUsername.Text.Trim(), TxtPassword.Password);
				}
				httpClientHandler.Proxy = webProxy;
				httpClientHandler.UseProxy = true;
			}
			using HttpClient client = new HttpClient(httpClientHandler)
			{
				Timeout = TimeSpan.FromSeconds(5.0)
			};
			AppConfig appConfig = ConfigManager.Load();
			string requestUri = "https://push2.eastmoney.com";
			HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, requestUri);
			HttpResponseMessage httpResponseMessage = await client.SendAsync(request);
			if (httpResponseMessage.IsSuccessStatusCode)
			{
				TxtTestResult.Text = $"✅ 连接成功! (HTTP {httpResponseMessage.StatusCode})";
				TxtTestResult.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
			}
			else
			{
				TxtTestResult.Text = $"❌ 目标拒绝 (HTTP {httpResponseMessage.StatusCode})";
				TxtTestResult.Foreground = new SolidColorBrush(Colors.Red);
			}
		}
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			TxtTestResult.Text = "❌ 连接失败: " + ex.Message;
			TxtTestResult.Foreground = new SolidColorBrush(Colors.Red);
		}
		finally
		{
			BtnTest.IsEnabled = true;
		}
	}

	private void BtnSave_Click(object sender, RoutedEventArgs e)
	{
		if (TglEnableProxy.IsChecked.GetValueOrDefault() && !int.TryParse(TxtPort.Text, out var _))
		{
			HandyControl.Controls.MessageBox.Show("端口号必须是数字！", "验证失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		AppConfig appConfig = ConfigManager.Load();
		appConfig.IsProxyEnabled = TglEnableProxy.IsChecked.GetValueOrDefault();
		appConfig.ProxyAddress = TxtAddress.Text.Trim();
		appConfig.ProxyPort = TxtPort.Text.Trim();
		appConfig.ProxyUserName = TxtUsername.Text.Trim();
		appConfig.ProxyPassword = TxtPassword.Password;
		ConfigManager.Save(appConfig);
		NetworkHelper.ReloadProxySettings();
		Growl.Success("网络与代理设置已应用！");
		base.DialogResult = true;
		Close();
	}

	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}

}
