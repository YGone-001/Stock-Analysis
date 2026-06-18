using System;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using AIHelper.Helpers;

namespace AIHelper.ViewModels;

public class LoginViewModel : INotifyPropertyChanged
{
	private readonly string _apiBaseUrl = "https://chat.98da.com/api/auth";

	private string _username;

	private string _errorMessage;

	public Action<string, string> OnLoginSuccess;

	public string Username
	{
		get
		{
			return _username;
		}
		set
		{
			_username = value;
			OnPropertyChanged("Username");
		}
	}

	public string ErrorMessage
	{
		get
		{
			return _errorMessage;
		}
		set
		{
			_errorMessage = value;
			OnPropertyChanged("ErrorMessage");
		}
	}

	public ICommand LoginCommand => new RelayCommand(async delegate(object o)
	{
		PasswordBox passwordBox = o as PasswordBox;
		if (string.IsNullOrWhiteSpace(Username) || passwordBox == null || string.IsNullOrWhiteSpace(passwordBox.Password))
		{
			ErrorMessage = "代号和暗号不能为空！";
			return;
		}
		ErrorMessage = "正在连接防空洞...";
		try
		{
			StringContent content = new StringContent(JsonSerializer.Serialize(new { Username, passwordBox.Password }), Encoding.UTF8, "application/json");
			using HttpClient client = new HttpClient();
			client.Timeout = TimeSpan.FromSeconds(5.0);
			HttpResponseMessage response = await client.PostAsync(_apiBaseUrl + "/login", content);
			string text = await response.Content.ReadAsStringAsync();
			if (response.IsSuccessStatusCode)
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(text);
				JsonElement rootElement = jsonDocument.RootElement;
				string @string = rootElement.GetProperty("Token").GetString();
				string string2 = rootElement.GetProperty("Username").GetString();
				OnLoginSuccess?.Invoke(@string, string2);
				AnalyticsService.Log("3", "1");
			}
			else
			{
				ErrorMessage = "登录失败: " + text;
			}
		}
		catch (Exception ex)
		{
			ErrorMessage = "网络异常: " + ex.Message;
		}
	});

	public ICommand RegisterCommand => new RelayCommand(async delegate(object o)
	{
		PasswordBox passwordBox = o as PasswordBox;
		if (string.IsNullOrWhiteSpace(Username) || passwordBox == null || string.IsNullOrWhiteSpace(passwordBox.Password))
		{
			ErrorMessage = "代号和暗号不能为空！";
			return;
		}
		ErrorMessage = "正在注册...";
		try
		{
			StringContent content = new StringContent(JsonSerializer.Serialize(new { Username, passwordBox.Password }), Encoding.UTF8, "application/json");
			using HttpClient client = new HttpClient();
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
		catch (Exception ex)
		{
			ErrorMessage = "网络异常: " + ex.Message;
		}
	});

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
