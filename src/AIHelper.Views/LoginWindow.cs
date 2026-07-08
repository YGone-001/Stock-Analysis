using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using AIHelper.Helpers;
using AIHelper.Models;

namespace AIHelper.Views;

public class LoginWindow : Window, IComponentConnector
{
	private readonly string _apiBaseUrl = ChatServiceConfig.BuildUrl("/api/auth");

	private static readonly byte[] s_additionalEntropy = Encoding.UTF8.GetBytes("CyberFish_2026_Salt");

	internal TextBox TxtUsername;

	internal PasswordBox TxtPassword;

	internal CheckBox ChkSaveUser;

	internal CheckBox ChkSavePwd;

	internal CheckBox ChkAutoLogin;

	internal TextBlock TxtError;

	internal Button BtnLogin;

	internal Button BtnCancel;

	internal Button BtnRegister;

	private bool _contentLoaded;

	public string LoggedInToken { get; private set; }

	public string LoggedInUsername { get; private set; }

	public LoginWindow()
	{
		InitializeComponent();
		LoadSavedCredentials();
	}

	private string Protect(string clearText)
	{
		if (string.IsNullOrEmpty(clearText))
		{
			return "";
		}
		try
		{
			return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(clearText), s_additionalEntropy, DataProtectionScope.CurrentUser));
		}
		catch
		{
			return "";
		}
	}

	private string Unprotect(string encryptedText)
	{
		if (string.IsNullOrEmpty(encryptedText))
		{
			return "";
		}
		try
		{
			byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(encryptedText), s_additionalEntropy, DataProtectionScope.CurrentUser);
			return Encoding.UTF8.GetString(bytes);
		}
		catch
		{
			return "";
		}
	}

	private void LoadSavedCredentials()
	{
		AppConfig appConfig = ConfigManager.Load();
		if (!string.IsNullOrEmpty(appConfig.SavedChatUser))
		{
			TxtUsername.Text = appConfig.SavedChatUser;
			ChkSaveUser.IsChecked = true;
		}
		if (!string.IsNullOrEmpty(appConfig.SavedChatPwd))
		{
			string password = Unprotect(appConfig.SavedChatPwd);
			TxtPassword.Password = password;
			ChkSavePwd.IsChecked = true;
		}
		ChkAutoLogin.IsChecked = appConfig.IsChatAutoLogin;
	}

	private void DragWindow(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left)
		{
			DragMove();
		}
	}

	private void Close_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}

	private async void BtnLogin_Click(object sender, RoutedEventArgs e)
	{
		string user = TxtUsername.Text.Trim();
		string pwd = TxtPassword.Password;
		if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pwd))
		{
			TxtError.Text = "代号和暗号不能为空！";
			return;
		}
		TxtError.Text = "正在验证通行证...";
		TxtError.Foreground = Brushes.Orange;
		BtnLogin.IsEnabled = false;
		try
		{
			StringContent content = new StringContent(JsonSerializer.Serialize(new
			{
				Username = user,
				Password = pwd
			}), Encoding.UTF8, "application/json");
			using HttpClient client = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(5.0)
			};
			HttpResponseMessage response = await client.PostAsync(_apiBaseUrl + "/login", content);
			string text = await response.Content.ReadAsStringAsync();
			if (response.IsSuccessStatusCode)
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(text);
				JsonElement rootElement = jsonDocument.RootElement;
				JsonElement value2;
				if (rootElement.TryGetProperty("Token", out var value))
				{
					LoggedInToken = value.GetString();
				}
				else if (rootElement.TryGetProperty("token", out value2))
				{
					LoggedInToken = value2.GetString();
				}
				JsonElement value4;
				if (rootElement.TryGetProperty("Username", out var value3))
				{
					LoggedInUsername = value3.GetString();
				}
				else if (rootElement.TryGetProperty("username", out value4))
				{
					LoggedInUsername = value4.GetString();
				}
				SavePreferences(user, pwd);
				base.DialogResult = true;
				Close();
			}
			else
			{
				if (text.Trim().StartsWith("<"))
				{
					TxtError.Text = "网关拦截: 可能是 404/502/403，请检查 Nginx 配置";
					MessageBox.Show("Nginx 返回了 HTML 页面，请检查服务器配置或看输出窗口。");
				}
				else
				{
					TxtError.Text = "验证拒绝: " + text;
				}
				TxtError.Foreground = Brushes.Red;
			}
		}
		catch (Exception ex)
		{
			TxtError.Text = "网络异常: " + ex.Message;
			TxtError.Foreground = Brushes.Red;
		}
		finally
		{
			BtnLogin.IsEnabled = true;
		}
	}

	private void SavePreferences(string user, string pwd)
	{
		AppConfig appConfig = ConfigManager.Load();
		appConfig.SavedChatUser = (ChkSaveUser.IsChecked.GetValueOrDefault() ? user : "");
		appConfig.SavedChatPwd = (ChkSavePwd.IsChecked.GetValueOrDefault() ? Protect(pwd) : "");
		appConfig.IsChatAutoLogin = ChkAutoLogin.IsChecked.GetValueOrDefault();
		ConfigManager.Save(appConfig);
	}

	private async void BtnRegister_Click(object sender, RoutedEventArgs e)
	{
		string text = TxtUsername.Text.Trim();
		string password = TxtPassword.Password;
		if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(password))
		{
			TxtError.Text = "代号和暗号不能为空！";
			return;
		}
		TxtError.Text = "正在登记新身份...";
		BtnRegister.IsEnabled = false;
		try
		{
			StringContent content = new StringContent(JsonSerializer.Serialize(new
			{
				Username = text,
				Password = password
			}), Encoding.UTF8, "application/json");
			using HttpClient client = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(5.0)
			};
			HttpResponseMessage response = await client.PostAsync(_apiBaseUrl + "/register", content);
			string text2 = await response.Content.ReadAsStringAsync();
			if (response.IsSuccessStatusCode)
			{
				TxtError.Text = "✅ 登记成功！请直接登录。";
				TxtError.Foreground = Brushes.Green;
			}
			else
			{
				TxtError.Text = "登记失败: " + text2;
				TxtError.Foreground = Brushes.Red;
			}
		}
		catch (Exception ex)
		{
			TxtError.Text = "异常: " + ex.Message;
		}
		finally
		{
			BtnRegister.IsEnabled = true;
		}
	}

	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/loginwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((Border)target).MouseLeftButtonDown += DragWindow;
			break;
		case 2:
			((Button)target).Click += Close_Click;
			break;
		case 3:
			TxtUsername = (TextBox)target;
			break;
		case 4:
			TxtPassword = (PasswordBox)target;
			break;
		case 5:
			ChkSaveUser = (CheckBox)target;
			break;
		case 6:
			ChkSavePwd = (CheckBox)target;
			break;
		case 7:
			ChkAutoLogin = (CheckBox)target;
			break;
		case 8:
			TxtError = (TextBlock)target;
			break;
		case 9:
			BtnLogin = (Button)target;
			BtnLogin.Click += BtnLogin_Click;
			break;
		case 10:
			BtnCancel = (Button)target;
			BtnCancel.Click += Close_Click;
			break;
		case 11:
			BtnRegister = (Button)target;
			BtnRegister.Click += BtnRegister_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
