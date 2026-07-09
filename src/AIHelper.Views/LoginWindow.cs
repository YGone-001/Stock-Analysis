using System;

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

public partial class LoginWindow : Window
{
	private readonly string _apiBaseUrl = ChatServiceConfig.BuildUrl("/api/auth");

	public string LoggedInToken { get; private set; }

	public string LoggedInUsername { get; private set; }

	public LoginWindow()
	{
		InitializeComponent();
		LoadSavedCredentials();
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
			string password = appConfig.SavedChatPwd;
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
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
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
		appConfig.SavedChatPwd = (ChkSavePwd.IsChecked.GetValueOrDefault() ? pwd : "");
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
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			TxtError.Text = "异常: " + ex.Message;
		}
		finally
		{
			BtnRegister.IsEnabled = true;
		}
	}

}
