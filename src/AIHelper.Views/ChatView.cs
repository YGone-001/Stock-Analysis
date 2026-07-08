using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.ViewModels;
using HandyControl.Controls;
using Serilog;

namespace AIHelper.Views;

public class ChatView : UserControl, IComponentConnector, IStyleConnector
{
	private System.Windows.Controls.ScrollViewer _messageScrollViewer;

	private bool _isLoadingHistory;

	internal Grid ChatMainUI;

	internal ListBox MessageList;

	internal Button BtnEmoji;

	internal Popup EmojiPopup;

	internal System.Windows.Controls.TextBox TxtInput;

	internal Popup AvatarPickerPopup;

	internal ItemsControl AvatarItemsControl;

	internal Border LoginOverlay;

	private bool _contentLoaded;

	public ChatView()
	{
		InitializeComponent();
		base.Loaded += ChatView_Loaded;
		base.PreviewKeyDown += ChatView_PreviewKeyDown;
		base.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, delegate(object s, ExecutedRoutedEventArgs e)
		{
			if (e.Parameter is string text)
			{
				try
				{
					Clipboard.SetText(text);
					Growl.Success("已复制到剪贴板");
				}
				catch (COMException)
				{
					Growl.Warning("剪贴板正被其他软件独占，请再点一次！");
				}
				catch (Exception ex2)
				{
					Growl.Error("复制失败: " + ex2.Message);
				}
			}
		}));
	}

	private async void ChatView_Loaded(object sender, RoutedEventArgs e)
	{
		AppConfig appConfig = ConfigManager.Load();
		if (!ChatServiceConfig.IsEnabled)
		{
			ChatMainUI.Visibility = Visibility.Collapsed;
			LoginOverlay.Visibility = Visibility.Visible;
			return;
		}
		if (appConfig.IsChatAutoLogin && !string.IsNullOrEmpty(appConfig.SavedChatUser) && !string.IsNullOrEmpty(appConfig.SavedChatPwd))
		{
			await PerformSilentLogin(appConfig);
		}
	}

	private void ChatImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount != 2 || !(sender is Image image))
		{
			return;
		}
		e.Handled = true;
		if (!(image.DataContext is ChatMessageModel chatMessageModel) || !chatMessageModel.IsImage)
		{
			return;
		}
		try
		{
			if (chatMessageModel.Content.StartsWith("/uploads") || chatMessageModel.Content.StartsWith("http"))
			{
				new ImageBrowser(new Uri(chatMessageModel.Content.StartsWith("http") ? chatMessageModel.Content : ChatServiceConfig.BuildUrl(chatMessageModel.Content))).Show();
				return;
			}
			string tempPath = Path.GetTempPath();
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(13, 1);
			defaultInterpolatedStringHandler.AppendLiteral("chat_img_");
			defaultInterpolatedStringHandler.AppendFormatted(chatMessageModel.Id);
			defaultInterpolatedStringHandler.AppendLiteral(".png");
			string text = Path.Combine(tempPath, defaultInterpolatedStringHandler.ToStringAndClear());
			if (!File.Exists(text))
			{
				byte[] bytes = Convert.FromBase64String(chatMessageModel.Content);
				File.WriteAllBytes(text, bytes);
			}
			new ImageBrowser(new Uri(text)).Show();
		}
		catch (Exception ex)
		{
			Growl.Error("图片打开失败: " + ex.Message);
		}
	}

	private System.Windows.Controls.ScrollViewer GetScrollViewer(DependencyObject depObj)
	{
		if (depObj is System.Windows.Controls.ScrollViewer result)
		{
			return result;
		}
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
			System.Windows.Controls.ScrollViewer scrollViewer = GetScrollViewer(child);
			if (scrollViewer != null)
			{
				return scrollViewer;
			}
		}
		return null;
	}

	private void ScrollToBottom()
	{
		Application.Current?.Dispatcher.InvokeAsync(delegate
		{
			if (_messageScrollViewer == null)
			{
				_messageScrollViewer = GetScrollViewer(MessageList);
				if (_messageScrollViewer != null)
				{
					_messageScrollViewer.ScrollChanged -= MessageScrollViewer_ScrollChanged;
					_messageScrollViewer.ScrollChanged += MessageScrollViewer_ScrollChanged;
				}
			}
			MessageList.UpdateLayout();
			if (_messageScrollViewer != null)
			{
				_messageScrollViewer.ScrollToBottom();
			}
			else if (MessageList.Items.Count > 0)
			{
				MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1]);
			}
		}, DispatcherPriority.ContextIdle);
	}

	private void BtnEmoji_Click(object sender, RoutedEventArgs e)
	{
		EmojiPopup.IsOpen = true;
	}

	private void LocalEmoji_Click(object sender, MouseButtonEventArgs e)
	{
		if (!(sender is Border border) || !(border.Tag is string path))
		{
			return;
		}
		EmojiPopup.IsOpen = false;
		if (!(base.DataContext is ChatViewModel chatViewModel))
		{
			return;
		}
		try
		{
			if (int.TryParse(Path.GetFileNameWithoutExtension(path), out var result))
			{
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(8, 1);
				defaultInterpolatedStringHandler.AppendLiteral("[emoji:");
				defaultInterpolatedStringHandler.AppendFormatted(result, "D2");
				defaultInterpolatedStringHandler.AppendLiteral("]");
				string text = defaultInterpolatedStringHandler.ToStringAndClear();
				chatViewModel.InputText = (string.IsNullOrEmpty(chatViewModel.InputText) ? text : (chatViewModel.InputText + text));
				TxtInput.Focus();
				TxtInput.CaretIndex = TxtInput.Text.Length;
			}
		}
		catch (Exception ex)
		{
			Growl.Error("表情插入失败: " + ex.Message);
		}
	}

	private void TxtInput_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			if (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == ModifierKeys.Shift)
			{
				int caretIndex = TxtInput.CaretIndex;
				TxtInput.Text = TxtInput.Text.Insert(caretIndex, Environment.NewLine);
				TxtInput.CaretIndex = caretIndex + Environment.NewLine.Length;
				e.Handled = true;
				return;
			}
			e.Handled = true;
			if (base.DataContext is ChatViewModel chatViewModel && chatViewModel.SendMessageCommand.CanExecute(null))
			{
				chatViewModel.SendMessageCommand.Execute(null);
			}
		}
		if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || (!Clipboard.ContainsImage() && !Clipboard.ContainsData("PNG")))
		{
			return;
		}
		e.Handled = true;
		if (!(base.DataContext is ChatViewModel chatViewModel2))
		{
			return;
		}
		try
		{
			byte[] array = null;
			if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream memoryStream)
			{
				array = memoryStream.ToArray();
			}
			if (array == null && Clipboard.ContainsImage())
			{
				BitmapSource image = Clipboard.GetImage();
				if (image != null)
				{
					FormatConvertedBitmap source = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0.0);
					using MemoryStream memoryStream2 = new MemoryStream();
					PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
					pngBitmapEncoder.Frames.Add(BitmapFrame.Create(source));
					pngBitmapEncoder.Save(memoryStream2);
					array = memoryStream2.ToArray();
				}
			}
			if (array != null)
			{
				if (array.Length > 5242880)
				{
					Growl.Warning("剪贴板图片超 5MB！");
				}
				else
				{
					_ = chatViewModel2.SendDirectImageAsync(Convert.ToBase64String(array));
				}
			}
		}
		catch (Exception ex)
		{
			Growl.Error("图片解析失败: " + ex.Message);
		}
	}

	private void ChatView_PreviewKeyDown(object sender, KeyEventArgs e)
	{
	}

	private async void MessageScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (e.VerticalChange < 0.0 && _messageScrollViewer.VerticalOffset == 0.0 && !_isLoadingHistory && base.DataContext is ChatViewModel chatViewModel)
		{
			_isLoadingHistory = true;
			object topItem = ((MessageList.Items.Count > 0) ? MessageList.Items[0] : null);
			await chatViewModel.LoadMoreHistoryAsync();
			if (topItem != null)
			{
				MessageList.ScrollIntoView(topItem);
			}
			_isLoadingHistory = false;
		}
	}

	private async Task PerformSilentLogin(AppConfig config)
	{
		_ = 1;
		try
		{
			string password = Unprotect(config.SavedChatPwd);
			StringContent content = new StringContent(JsonSerializer.Serialize(new
			{
				Username = config.SavedChatUser,
				Password = password
			}), Encoding.UTF8, "application/json");
			using HttpClient client = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(5.0)
			};
			HttpResponseMessage httpResponseMessage = await client.PostAsync(ChatServiceConfig.BuildUrl("/api/auth/login"), content);
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
				return;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(await httpResponseMessage.Content.ReadAsStringAsync());
			JsonElement rootElement = jsonDocument.RootElement;
			string token = "";
			JsonElement value2;
			if (rootElement.TryGetProperty("Token", out var value))
			{
				token = value.GetString();
			}
			else if (rootElement.TryGetProperty("token", out value2))
			{
				token = value2.GetString();
			}
			LoginOverlay.Visibility = Visibility.Collapsed;
			ChatMainUI.Visibility = Visibility.Visible;
			MainViewModel mainViewModel = (base.DataContext as MainViewModel) ?? (System.Windows.Window.GetWindow(this)?.DataContext as MainViewModel) ?? (Application.Current.MainWindow?.DataContext as MainViewModel);
			ChatViewModel chatVM = (ChatViewModel)(base.DataContext = new ChatViewModel(mainViewModel, config.SavedChatUser, token));
			if (mainViewModel != null)
			{
				mainViewModel.ChatVM = chatVM;
			}
		}
		catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
	}

	private string Unprotect(string encryptedText)
	{
		try
		{
			byte[] encryptedData = Convert.FromBase64String(encryptedText);
			byte[] bytes = Encoding.UTF8.GetBytes("CyberFish_2026_Salt");
			byte[] bytes2 = ProtectedData.Unprotect(encryptedData, bytes, DataProtectionScope.CurrentUser);
			return Encoding.UTF8.GetString(bytes2);
		}
		catch
		{
			return "";
		}
	}

	private async void BtnLogout_Click(object sender, RoutedEventArgs e)
	{
		AppConfig appConfig = ConfigManager.Load();
		appConfig.IsChatAutoLogin = false;
		ConfigManager.Save(appConfig);
		ChatMainUI.Visibility = Visibility.Collapsed;
		LoginOverlay.Visibility = Visibility.Visible;
		if (base.DataContext is ChatViewModel chatViewModel)
		{
			await chatViewModel.DisconnectAsync();
		}
		if (System.Windows.Window.GetWindow(this)?.DataContext is MainViewModel mainViewModel)
		{
			mainViewModel.ChatVM = null;
			mainViewModel.StatusRight = "在线人数: 已注销";
		}
		base.DataContext = null;
	}

	private void BtnShowLogin_Click(object sender, RoutedEventArgs e)
	{
		if (!ChatServiceConfig.IsEnabled)
		{
			Growl.Info("聊天服务为可选功能，当前未配置。");
			return;
		}
		LoginWindow loginWindow = new LoginWindow();
		loginWindow.Owner = System.Windows.Window.GetWindow(this);
		if (loginWindow.ShowDialog().GetValueOrDefault())
		{
			LoginOverlay.Visibility = Visibility.Collapsed;
			ChatMainUI.Visibility = Visibility.Visible;
			MainViewModel mainViewModel = (base.DataContext as MainViewModel) ?? (System.Windows.Window.GetWindow(this)?.DataContext as MainViewModel) ?? (Application.Current.MainWindow?.DataContext as MainViewModel);
			ChatViewModel chatVM = (ChatViewModel)(base.DataContext = new ChatViewModel(mainViewModel, loginWindow.LoggedInUsername, loginWindow.LoggedInToken));
			if (mainViewModel != null)
			{
				mainViewModel.ChatVM = chatVM;
			}
		}
	}

	private void UserControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (e.OldValue is ChatViewModel chatViewModel)
		{
			chatViewModel.ScrollToBottomRequested -= Vm_ScrollToBottomRequested;
			chatViewModel.InitialHistoryLoaded -= Vm_InitialHistoryLoaded;
			_ = chatViewModel.DisconnectAsync();
		}
		if (e.NewValue is ChatViewModel chatViewModel2)
		{
			chatViewModel2.ScrollToBottomRequested += Vm_ScrollToBottomRequested;
			chatViewModel2.InitialHistoryLoaded += Vm_InitialHistoryLoaded;
		}
	}

	private void Vm_ScrollToBottomRequested()
	{
		if (!_isLoadingHistory)
		{
			ScrollToBottom();
		}
	}

	private void Vm_InitialHistoryLoaded()
	{
		ScrollToBottom();
	}

	private void LoadAvatarList()
	{
		List<string> list = new List<string>();
		for (int i = 1; i <= 25; i++)
		{
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(39, 1);
			defaultInterpolatedStringHandler.AppendLiteral("pack://application:,,,/Assets/face/");
			defaultInterpolatedStringHandler.AppendFormatted(i, "D2");
			defaultInterpolatedStringHandler.AppendLiteral(".png");
			list.Add(defaultInterpolatedStringHandler.ToStringAndClear());
		}
		AvatarItemsControl.ItemsSource = list;
	}

	private void MyAvatar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (AvatarItemsControl.ItemsSource == null)
		{
			LoadAvatarList();
		}
		AvatarPickerPopup.PlacementTarget = sender as UIElement;
		AvatarPickerPopup.IsOpen = true;
		e.Handled = true;
	}

	private async void SelectLocalAvatar_Click(object sender, MouseButtonEventArgs e)
	{
		if (sender is Border border && border.Tag is string path)
		{
			AvatarPickerPopup.IsOpen = false;
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
			if (base.DataContext is ChatViewModel chatViewModel)
			{
				await chatViewModel.UpdateAvatarAsync(fileNameWithoutExtension);
				Growl.Success("头像修改成功。");
			}
		}
	}

	private async void ResetGravatar_Click(object sender, MouseButtonEventArgs e)
	{
		AvatarPickerPopup.IsOpen = false;
		if (base.DataContext is ChatViewModel chatViewModel)
		{
			await chatViewModel.UpdateAvatarAsync("");
			Growl.Success("已恢复默认头像。");
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/chatview.xaml", UriKind.Relative);
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
			((ChatView)target).DataContextChanged += UserControl_DataContextChanged;
			break;
		case 2:
			ChatMainUI = (Grid)target;
			break;
		case 3:
			((Button)target).Click += BtnLogout_Click;
			break;
		case 4:
			MessageList = (ListBox)target;
			break;
		case 7:
			BtnEmoji = (Button)target;
			BtnEmoji.Click += BtnEmoji_Click;
			break;
		case 8:
			EmojiPopup = (Popup)target;
			break;
		case 10:
			TxtInput = (System.Windows.Controls.TextBox)target;
			TxtInput.PreviewKeyDown += TxtInput_PreviewKeyDown;
			break;
		case 11:
			AvatarPickerPopup = (Popup)target;
			break;
		case 12:
			AvatarItemsControl = (ItemsControl)target;
			break;
		case 14:
			((Border)target).MouseLeftButtonDown += ResetGravatar_Click;
			break;
		case 15:
			LoginOverlay = (Border)target;
			break;
		case 16:
			((Button)target).Click += BtnShowLogin_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IStyleConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 5:
			((Image)target).MouseLeftButtonDown += ChatImage_MouseLeftButtonDown;
			break;
		case 6:
			((Grid)target).MouseLeftButtonDown += MyAvatar_MouseLeftButtonDown;
			break;
		case 9:
			((Border)target).MouseLeftButtonDown += LocalEmoji_Click;
			break;
		case 13:
			((Border)target).MouseLeftButtonDown += SelectLocalAvatar_Click;
			break;
		}
	}
}
