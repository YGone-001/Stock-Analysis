using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AIHelper.Helpers;
using AIHelper.Models;
using HandyControl.Controls;

namespace AIHelper.Views;

public class DataSourceWindow : HandyControl.Controls.Window, IComponentConnector
{
	internal RadioButton RdoHttps;

	internal RadioButton RdoHttp;

	internal RadioButton RdoCustom;

	internal System.Windows.Controls.TextBox TxtCustomUrl;

	internal Button BtnCancel;

	internal Button BtnSave;

	private bool _contentLoaded;

	public DataSourceWindow()
	{
		InitializeComponent();
		base.Loaded += DataSourceWindow_Loaded;
	}

	private void DataSourceWindow_Loaded(object sender, RoutedEventArgs e)
	{
		string text = ConfigManager.Load().AkServerUrl?.TrimEnd('/');
		if (string.IsNullOrEmpty(text) || text.Equals("https://www.98da.com", StringComparison.OrdinalIgnoreCase))
		{
			RdoHttps.IsChecked = true;
			return;
		}
		if (text.Equals("http://www.98da.com", StringComparison.OrdinalIgnoreCase))
		{
			RdoHttp.IsChecked = true;
			return;
		}
		RdoCustom.IsChecked = true;
		TxtCustomUrl.Text = text;
	}

	private void BtnSave_Click(object sender, RoutedEventArgs e)
	{
		string text = "";
		if (RdoHttps.IsChecked.GetValueOrDefault())
		{
			text = "https://www.98da.com";
		}
		else if (RdoHttp.IsChecked.GetValueOrDefault())
		{
			text = "http://www.98da.com";
		}
		else
		{
			text = TxtCustomUrl.Text.Trim().TrimEnd('/');
			if (string.IsNullOrEmpty(text) || (!text.StartsWith("http://") && !text.StartsWith("https://")))
			{
				HandyControl.Controls.MessageBox.Show("请输入正确的 URL 地址（必须以 http:// 或 https:// 开头）！", "格式错误", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
		}
		AppConfig appConfig = ConfigManager.Load();
		appConfig.AkServerUrl = text;
		ConfigManager.Save(appConfig);
		Growl.Success("数据源已成功切换至: " + text);
		AnalyticsService.Log("11", text ?? "");
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
			Uri resourceLocator = new Uri("/AIHelper;component/views/datasourcewindow.xaml", UriKind.Relative);
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
			RdoHttps = (RadioButton)target;
			break;
		case 2:
			RdoHttp = (RadioButton)target;
			break;
		case 3:
			RdoCustom = (RadioButton)target;
			break;
		case 4:
			TxtCustomUrl = (System.Windows.Controls.TextBox)target;
			break;
		case 5:
			BtnCancel = (Button)target;
			BtnCancel.Click += BtnCancel_Click;
			break;
		case 6:
			BtnSave = (Button)target;
			BtnSave.Click += BtnSave_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
