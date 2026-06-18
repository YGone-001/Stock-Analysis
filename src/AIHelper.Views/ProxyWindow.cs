using System;
using System.CodeDom.Compiler;
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

public class ProxyWindow : HandyControl.Controls.Window, IComponentConnector
{
	internal ToggleButton TglEnableProxy;

	internal System.Windows.Controls.TextBox TxtAddress;

	internal System.Windows.Controls.TextBox TxtPort;

	internal System.Windows.Controls.TextBox TxtUsername;

	internal System.Windows.Controls.PasswordBox TxtPassword;

	internal TextBlock TxtTestResult;

	internal Button BtnTest;

	internal Button BtnCancel;

	internal Button BtnSave;

	private bool _contentLoaded;

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
			string requestUri = (string.IsNullOrWhiteSpace(appConfig.AkServerUrl) ? "https://www.98da.com" : appConfig.AkServerUrl);
			HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, requestUri);
			HttpResponseMessage httpResponseMessage = await client.SendAsync(request);
			if (httpResponseMessage.IsSuccessStatusCode)
			{
				TextBlock txtTestResult = TxtTestResult;
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(15, 1);
				defaultInterpolatedStringHandler.AppendLiteral("✅ 连接成功! (HTTP ");
				defaultInterpolatedStringHandler.AppendFormatted(httpResponseMessage.StatusCode);
				defaultInterpolatedStringHandler.AppendLiteral(")");
				txtTestResult.Text = defaultInterpolatedStringHandler.ToStringAndClear();
				TxtTestResult.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
			}
			else
			{
				TextBlock txtTestResult2 = TxtTestResult;
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(14, 1);
				defaultInterpolatedStringHandler.AppendLiteral("❌ 目标拒绝 (HTTP ");
				defaultInterpolatedStringHandler.AppendFormatted(httpResponseMessage.StatusCode);
				defaultInterpolatedStringHandler.AppendLiteral(")");
				txtTestResult2.Text = defaultInterpolatedStringHandler.ToStringAndClear();
				TxtTestResult.Foreground = new SolidColorBrush(Colors.Red);
			}
		}
		catch (Exception ex)
		{
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
		appConfig.ProxyPort = (int.TryParse(TxtPort.Text.Trim(), out var result2) ? result2 : 7890);
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

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/proxywindow.xaml", UriKind.Relative);
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
			TglEnableProxy = (ToggleButton)target;
			break;
		case 2:
			TxtAddress = (System.Windows.Controls.TextBox)target;
			break;
		case 3:
			TxtPort = (System.Windows.Controls.TextBox)target;
			break;
		case 4:
			TxtUsername = (System.Windows.Controls.TextBox)target;
			break;
		case 5:
			TxtPassword = (System.Windows.Controls.PasswordBox)target;
			break;
		case 6:
			TxtTestResult = (TextBlock)target;
			break;
		case 7:
			BtnTest = (Button)target;
			BtnTest.Click += BtnTest_Click;
			break;
		case 8:
			BtnCancel = (Button)target;
			BtnCancel.Click += BtnCancel_Click;
			break;
		case 9:
			BtnSave = (Button)target;
			BtnSave.Click += BtnSave_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
