#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIHelper.Helpers;


namespace AIHelper.ViewModels;

public partial class LoginViewModel : ObservableObject
{
	private readonly string _apiBaseUrl = ChatServiceConfig.BuildUrl("/api/auth");

	[ObservableProperty]
	private string _username = string.Empty;

	[ObservableProperty]
	private string _errorMessage = string.Empty;

	public Action<string, string>? OnLoginSuccess;

	[RelayCommand]
	private async Task LoginAsync(object o)
	{
		PasswordBox? passwordBox = o as PasswordBox;
		if (string.IsNullOrWhiteSpace(Username) || passwordBox == null || string.IsNullOrWhiteSpace(passwordBox.Password))
		{
			ErrorMessage = "代号和暗号不能为空！";
			return;
		}
		ErrorMessage = "正在连接防空洞...";
		try
		{
			StringContent content = new StringContent(JsonSerializer.Serialize(new { Username, passwordBox.Password }), Encoding.UTF8, "application/json");
			HttpClient client = AIHelper.Helpers.NetworkHelper.SharedHttpClient;
			client.Timeout = TimeSpan.FromSeconds(5.0);
			HttpResponseMessage response = await client.PostAsync(_apiBaseUrl + "/login", content);
			string text = await response.Content.ReadAsStringAsync();
			if (response.IsSuccessStatusCode)
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(text);
				JsonElement rootElement = jsonDocument.RootElement;
				string? @string = rootElement.GetProperty("Token").GetString();
				string? string2 = rootElement.GetProperty("Username").GetString();
				if (!string.IsNullOrEmpty(@string) && !string.IsNullOrEmpty(string2))
				{
					OnLoginSuccess?.Invoke(@string, string2);
				}
				AnalyticsService.Log("3", "1");
			}
			else
			{
				ErrorMessage = "登录失败: " + text;
			}
		}
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			ErrorMessage = "网络异常: " + ex.Message;
		}
	}

	[RelayCommand]
	private async Task RegisterAsync(object o)
	{
		PasswordBox? passwordBox = o as PasswordBox;
		if (string.IsNullOrWhiteSpace(Username) || passwordBox == null || string.IsNullOrWhiteSpace(passwordBox.Password))
		{
			ErrorMessage = "代号和暗号不能为空！";
			return;
		}
		ErrorMessage = "正在注册...";
		try
		{
			StringContent content = new StringContent(JsonSerializer.Serialize(new { Username, passwordBox.Password }), Encoding.UTF8, "application/json");
			HttpClient client = AIHelper.Helpers.NetworkHelper.SharedHttpClient;
			client.Timeout = TimeSpan.FromSeconds(5.0);
			HttpResponseMessage response = await client.PostAsync(_apiBaseUrl + "/register", content);
			string text = await response.Content.ReadAsStringAsync();
			if (response.IsSuccessStatusCode)
			{
				ErrorMessage = "✅ 注册成功！请直接点击登录。";
				AnalyticsService.Log("3", "3");
			}
			else
			{
				ErrorMessage = "注册失败: " + text;
			}
		}
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			ErrorMessage = "网络异常: " + ex.Message;
		}
	}
}
