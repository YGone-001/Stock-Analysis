using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AIHelper.Helpers;
using RelayCommand = AIHelper.Helpers.RelayCommand;
using AIHelper.Models;
using HandyControl.Controls;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Win32;
using Serilog;

namespace AIHelper.ViewModels;

public partial class ChatViewModel : ObservableObject, IDisposable
{
	private ICommand? _changeRoomCommand;
	private ICommand? _replyCommand;
	private ICommand? _cancelReplyCommand;
	private ICommand? _atUserCommand;
	private ICommand? _increaseFontCommand;
	private ICommand? _decreaseFontCommand;
	private ICommand? _adminDeleteCommand;
	private ICommand? _adminBanCommand;
	private ICommand? _adminBanAccountCommand;
	private ICommand? _sendMessageCommand;
	private ICommand? _recallCommand;
	private ICommand? _shareMarketCommand;
	private ICommand? _clickStockCodeCommand;
	private ICommand? _sendImageCommand;
	public HubConnection _connection;

	private readonly MainViewModel _mainVm;

	public readonly string _myNickName;

	private readonly string _token;

	private bool _isAdmin;

	private ChatRoomModel _selectedGroup;

	private string _inputText;

	private ChatMessageModel _replyingMessage;

	private string _systemNoticeText = "暂无重要公告，摸鱼愉快！";

	private int _chatFontSize = 14;

	private volatile bool _isSending;

	private const int PAGE_SIZE = 50;

	private bool _isUploadingImage;

	public bool IsAdmin
	{
		get
		{
			return _isAdmin;
		}
		set
		{
			_isAdmin = value;
			OnPropertyChanged(nameof(IsAdmin));
		}
	}

	public ObservableCollection<ChatRoomModel> Groups { get; } = new ObservableCollection<ChatRoomModel>
	{
		new ChatRoomModel
		{
			Name = "随便聊聊",
			IsSelected = true
		},
		new ChatRoomModel
		{
			Name = "股票交流"
		},
		new ChatRoomModel
		{
			Name = "软件交流"
		},
		new ChatRoomModel
		{
			Name = "AI交流"
		}
	};


	public ChatRoomModel SelectedGroup
	{
		get
		{
			return _selectedGroup;
		}
		set
		{
			if (_selectedGroup == value)
			{
				return;
			}
			if (_selectedGroup != null)
			{
				_selectedGroup.IsSelected = false;
			}
			_selectedGroup = value;
			if (_selectedGroup != null)
			{
				_selectedGroup.IsSelected = true;
				_selectedGroup.UnreadCount = 0;
			}
			OnPropertyChanged(nameof(SelectedGroup));
			if (_selectedGroup != null && !_selectedGroup.IsInitialLoaded)
			{
				HubConnection connection = _connection;
				if (connection != null && connection.State == HubConnectionState.Connected)
				{
					LoadInitialHistoryAsync(_selectedGroup).SafeFireAndForget();
					return;
				}
			}
			this.ScrollToBottomRequested?.Invoke();
		}
	}

	public List<string> EmojiList { get; } = Enumerable.Range(1, 47).Select(delegate(int i)
	{
		return $"pack://application:,,,/Assets/emoji/{i:D2}.png";
	}).ToList();


	public string InputText
	{
		get
		{
			return _inputText;
		}
		set
		{
			_inputText = value;
			OnPropertyChanged(nameof(InputText));
		}
	}

	public ChatMessageModel ReplyingMessage
	{
		get
		{
			return _replyingMessage;
		}
		set
		{
			_replyingMessage = value;
			OnPropertyChanged(nameof(ReplyingMessage));
			OnPropertyChanged(nameof(IsReplying));
		}
	}

	public bool IsReplying => ReplyingMessage != null;

	public string SystemNoticeText
	{
		get
		{
			return _systemNoticeText;
		}
		set
		{
			_systemNoticeText = value;
			OnPropertyChanged(nameof(SystemNoticeText));
		}
	}

	public int ChatFontSize
	{
		get
		{
			return _chatFontSize;
		}
		set
		{
			_chatFontSize = value;
			OnPropertyChanged(nameof(ChatFontSize));
		}
	}

	public bool IsUploadingImage
	{
		get
		{
			return _isUploadingImage;
		}
		set
		{
			_isUploadingImage = value;
			OnPropertyChanged(nameof(IsUploadingImage));
		}
	}

	public ICommand ChangeRoomCommand => _changeRoomCommand ??= new RelayCommand(delegate(object o)
	{
		if (o is ChatRoomModel chatRoomModel && chatRoomModel != SelectedGroup)
		{
			SelectedGroup = chatRoomModel;
		}
	});

	public ICommand ReplyCommand => _replyCommand ??= new RelayCommand(delegate(object msgObj)
	{
		if (msgObj is ChatMessageModel replyingMessage)
		{
			ReplyingMessage = replyingMessage;
			InputText = "";
		}
	});

	public ICommand CancelReplyCommand => _cancelReplyCommand ??= new RelayCommand(delegate
	{
		ReplyingMessage = null;
	});

	public ICommand AtUserCommand => _atUserCommand ??= new RelayCommand(delegate(object msgObj)
	{
		if (msgObj is ChatMessageModel chatMessageModel)
		{
			string text = "@" + chatMessageModel.SenderName + " ";
			if (string.IsNullOrEmpty(InputText))
			{
				InputText = text;
			}
			else
			{
				InputText = InputText + (InputText.EndsWith(" ") ? "" : " ") + text;
			}
		}
	});

	public ICommand IncreaseFontCommand => _increaseFontCommand ??= new RelayCommand(delegate
	{
		if (ChatFontSize < 24)
		{
			ChatFontSize++;
		}
	});

	public ICommand DecreaseFontCommand => _decreaseFontCommand ??= new RelayCommand(delegate
	{
		if (ChatFontSize > 10)
		{
			ChatFontSize--;
		}
	});

	public ICommand AdminDeleteCommand => _adminDeleteCommand ??= new RelayCommand(async delegate(object msgIdObj)
	{
		if (msgIdObj is int)
		{
			int num = (int)msgIdObj;
			try
			{
				Growl.Info($"[系统] 正在向服务器发送物理抹除指令 (ID:{num})...");
				await _connection.InvokeAsync("AdminDeleteMessage", _myNickName, num);
			}
			catch (Exception ex)
			{
				Growl.Error("物理删除失败: " + ex.Message);
			}
		}
	});

	public ICommand AdminBanCommand => _adminBanCommand ??= new RelayCommand(async delegate(object msgObj)
	{
		if (msgObj is ChatMessageModel chatMessageModel)
		{
			if (string.IsNullOrEmpty(chatMessageModel.SenderIp))
			{
				Growl.Warning("❌ 无法执法：该消息没有IP记录！");
			}
			else
			{
				string arg = (string.IsNullOrWhiteSpace(InputText) ? "管理员执法封禁" : InputText.Trim());
				if (System.Windows.MessageBox.Show($"确定要将 [{chatMessageModel.SenderName}] (IP: {chatMessageModel.SenderIp}) 封禁 7 天吗？", "执法确认", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
				{
					try
					{
						InputText = "";
						await _connection.InvokeAsync("AdminBanUser", _myNickName, chatMessageModel.SenderIp, 7, arg, chatMessageModel.SenderName);
					}
					catch (Exception ex)
					{
						Growl.Error("封禁失败: " + ex.Message);
					}
				}
			}
		}
	});

	public ICommand AdminBanAccountCommand => _adminBanAccountCommand ??= new RelayCommand(async delegate(object msgObj)
	{
		if (msgObj is ChatMessageModel chatMessageModel)
		{
			string text = (string.IsNullOrWhiteSpace(InputText) ? "严重违规，账号报废" : InputText.Trim());
			if (System.Windows.MessageBox.Show("⚠\ufe0f 极刑警告！\n\n确定要将账号 [" + chatMessageModel.SenderName + "] 【永久封禁】吗？\n封禁理由：" + text, "神罚确认", MessageBoxButton.YesNo, MessageBoxImage.Hand) == MessageBoxResult.Yes)
			{
				try
				{
					InputText = "";
					await _connection.InvokeAsync("AdminBanAccount", _myNickName, chatMessageModel.SenderName, text);
				}
				catch (Exception ex)
				{
					Growl.Error("账号封禁失败: " + ex.Message);
				}
			}
		}
	});

	public ICommand SendMessageCommand => _sendMessageCommand ??= new RelayCommand(async delegate
	{
		if (!_isSending && !string.IsNullOrWhiteSpace(InputText))
		{
			if (_connection.State != HubConnectionState.Connected)
			{
				Growl.Warning("❌ 尚未连接到聊天服务器，发送失败！", "ChatRoomGrowl");
			}
			else
			{
				string text = InputText.Trim();
				_isSending = true;
				try
				{
					_ = 1;
					try
					{
						if (IsAdmin && (text.StartsWith("/notice ") || text.StartsWith("/公告 ")))
						{
							string arg = text.Substring(text.IndexOf(' ')).Trim();
							await _connection.InvokeAsync("AdminPublishNotice", _myNickName, arg);
							InputText = "";
						}
						else
						{
							int? num = ReplyingMessage?.Id;
							await _connection.InvokeAsync("SendMessage", _myNickName, SelectedGroup.Name, "text", text, num);
							InputText = "";
							ReplyingMessage = null;
						}
					}
					catch (Exception ex)
					{
						Growl.Error("发送失败: " + ex.Message, "ChatRoomGrowl");
					}
				}
				finally
				{
					await Task.Delay(500);
					_isSending = false;
				}
			}
		}
	});

	public ICommand RecallCommand => _recallCommand ??= new RelayCommand(async delegate(object msgIdObj)
	{
		if (msgIdObj is int)
		{
			int num = (int)msgIdObj;
			try
			{
				await _connection.InvokeAsync("RecallMessage", num);
			}
			catch (Exception ex)
			{
				Growl.Error("撤回请求失败: " + ex.Message, "ChatRoomGrowl");
			}
		}
	});

	public ICommand ShareMarketCommand => _shareMarketCommand ??= new RelayCommand(delegate
	{
		StockModel stockModel = _mainVm?.StockVM?.CurrentSelectedStock;
		if (stockModel != null)
		{
			string text = $"【行情】{stockModel.Name}({stockModel.Code}) 现价:{stockModel.Price:F2} 涨幅:{stockModel.Percent:F2}%";
			InputText = (string.IsNullOrEmpty(InputText) ? text : (InputText + "\n" + text));
		}
		else
		{
			Growl.Warning("请先选中一只股票！", "ChatRoomGrowl");
		}
	});

	public ICommand ClickStockCodeCommand => _clickStockCodeCommand ??= new RelayCommand(delegate(object codeObj)
	{
		if (codeObj is string text)
		{
			_mainVm?.StockVM?.AddStockFromChat(text);
			Growl.Success("已添加 [" + text + "]", "ChatRoomGrowl");
		}
	});

	public ICommand SendImageCommand => _sendImageCommand ??= new RelayCommand(async delegate
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "图片文件|*.jpg;*.jpeg;*.png;*.gif"
		};
		if (openFileDialog.ShowDialog().GetValueOrDefault())
		{
			try
			{
				byte[] array = File.ReadAllBytes(openFileDialog.FileName);
				if (array.Length > 5218304)
				{
					Growl.Warning("图片过大！", "ChatRoomGrowl");
				}
				else
				{
					await SendDirectImageAsync(Convert.ToBase64String(array));
				}
			}
			catch (Exception ex)
			{
				Growl.Error("图片解析失败: " + ex.Message, "ChatRoomGrowl");
			}
		}
	});

	public event Action<int> OnlineCountUpdated;

	public event Action InitialHistoryLoaded;

	public event Action ScrollToBottomRequested;

	

	public ChatViewModel(MainViewModel mainVm, string username, string token)
	{
		_mainVm = mainVm;
		_myNickName = username;
		_token = token;
		SelectedGroup = Groups[0];
		InitializeSignalR().SafeFireAndForget();
	}

	private async Task InitializeSignalR()
	{
		_connection = ((IHubConnectionBuilder)new HubConnectionBuilder()).WithUrl(ChatServiceConfig.BuildUrl("/chatHub"), (Action<HttpConnectionOptions>)delegate(HttpConnectionOptions options)
		{
			options.AccessTokenProvider = () => Task.FromResult(_token);
		}).WithAutomaticReconnect().Build();
		_connection.On("ReceiveMessage", delegate(ServerChatMessage serverMsg)
		{
			ServerChatMessage serverMsg2 = serverMsg;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				string safeServerGroup = serverMsg2.GroupName?.Trim().ToLower() ?? "";
				ChatRoomModel chatRoomModel = Groups.FirstOrDefault((ChatRoomModel g) => g.Name.Trim().ToLower() == safeServerGroup);
				if (chatRoomModel != null)
				{
					if (!chatRoomModel.Messages.Any((ChatMessageModel m) => m.Id == serverMsg2.Id && serverMsg2.Id > 0))
					{
						chatRoomModel.Messages.Add(new ChatMessageModel
						{
							Id = serverMsg2.Id,
							SenderName = serverMsg2.SenderName,
							SenderIp = serverMsg2.SenderIp,
							Content = serverMsg2.Content,
							MsgType = serverMsg2.MsgType,
							SendTime = serverMsg2.SendTime,
							IsWithdrawn = serverMsg2.IsWithdrawn,
							IsSelf = (serverMsg2.SenderName == _myNickName),
							QuoteContent = serverMsg2.QuoteContent,
							QuoteSender = serverMsg2.QuoteSender,
							Avatar = serverMsg2.Avatar
						});
					}
					if (chatRoomModel == SelectedGroup)
					{
						this.ScrollToBottomRequested?.Invoke();
						if (serverMsg2.Id > 0)
						{
							SaveLastReadId(serverMsg2.Id);
						}
					}
					else
					{
						chatRoomModel.UnreadCount++;
					}
					if (!serverMsg2.IsWithdrawn && serverMsg2.SenderName != _myNickName && IsMentioned(serverMsg2.Content))
					{
						Growl.Info("[" + serverMsg2.SenderName + "] @ 了你，快去看看！", "ChatRoomGrowl");
					}
				}
			});
		});
		_connection.On("UpdateOnlineCount", delegate(int count)
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				this.OnlineCountUpdated?.Invoke(count);
			});
		});
		_connection.On("SystemNotice", delegate(string notice)
		{
			string notice3 = notice;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				Growl.Warning(notice3, "ChatRoomGrowl");
			});
		});
		_connection.On("ReceiveNotice", delegate(string notice)
		{
			string notice2 = notice;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				SystemNoticeText = notice2;
			});
		});
		_connection.On("AdminMessageDeleted", delegate(int msgId)
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				foreach (ChatRoomModel group in Groups)
				{
					ChatMessageModel chatMessageModel2 = group.Messages.FirstOrDefault((ChatMessageModel m) => m.Id == msgId);
					if (chatMessageModel2 != null)
					{
						group.Messages.Remove(chatMessageModel2);
						break;
					}
				}
			});
		});
		_connection.On("MessageRecalled", delegate(int msgId)
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				foreach (ChatRoomModel group2 in Groups)
				{
					ChatMessageModel chatMessageModel = group2.Messages.FirstOrDefault((ChatMessageModel m) => m.Id == msgId);
					if (chatMessageModel != null)
					{
						int index = group2.Messages.IndexOf(chatMessageModel);
						group2.Messages[index] = new ChatMessageModel
						{
							Id = chatMessageModel.Id,
							SenderName = chatMessageModel.SenderName,
							SenderIp = chatMessageModel.SenderIp,
							MsgType = "system",
							Content = "\ud83d\udeab [该消息已被撤回]",
							SendTime = chatMessageModel.SendTime,
							IsWithdrawn = true,
							IsSelf = chatMessageModel.IsSelf,
							Avatar = chatMessageModel.Avatar
						};
						break;
					}
				}
			});
		});
		_connection.On("ForceLogout", async delegate(string targetName)
		{
			if (targetName == _myNickName)
			{
				Application.Current?.Dispatcher.Invoke(delegate
				{
					Growl.Fatal("\ud83d\udea8 您的账号已被强制踢出！", "ChatRoomGrowl");
				});
				await DisconnectAsync();
			}
		});
		_connection.On("UserAvatarChanged", delegate(string targetUserName, string newAvatarCode)
		{
			string targetUserName2 = targetUserName;
			string newAvatarCode2 = newAvatarCode;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				foreach (ChatRoomModel group3 in Groups)
				{
					foreach (ChatMessageModel message in group3.Messages)
					{
						if (message.SenderName == targetUserName2)
						{
							message.Avatar = newAvatarCode2;
						}
					}
				}
			});
		});
		try
		{
			await _connection.StartAsync();
			foreach (ChatRoomModel group4 in Groups)
			{
				await _connection.InvokeAsync("JoinGroup", group4.Name);
			}
			IsAdmin = await _connection.InvokeAsync<bool>("CheckIsAdmin", _myNickName);
			if (SelectedGroup != null && !SelectedGroup.IsInitialLoaded)
			{
				await LoadInitialHistoryAsync(SelectedGroup);
			}
		}
		catch (Exception ex2)
		{
			Exception ex = ex2;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				Growl.Error("❌ 聊天室连接失败: " + ex.Message, "ChatRoomGrowl");
			});
		}
	}

	private async Task LoadInitialHistoryAsync(ChatRoomModel room)
	{
		ChatRoomModel room2 = room;
		if (room2 == null)
		{
			return;
		}
		try
		{
			room2.CurrentSkip = 0;
			List<ServerChatMessage> history = await _connection.InvokeAsync<List<ServerChatMessage>>("GetHistory", room2.Name, room2.CurrentSkip, 50);
			Application.Current?.Dispatcher.Invoke(delegate
			{
				room2.Messages.Clear();
				int num = 0;
				int lastReadId = GetLastReadId();

				int num2 = lastReadId;
				if (history != null)
				{
					foreach (ServerChatMessage msg in history)
					{
						if (!room2.Messages.Any((ChatMessageModel m) => m.Id == msg.Id && msg.Id > 0))
						{
							room2.Messages.Add(new ChatMessageModel
							{
								Id = msg.Id,
								SenderName = msg.SenderName,
								SenderIp = msg.SenderIp,
								Content = (msg.IsWithdrawn ? "\ud83d\udeab [该消息已被撤回]" : msg.Content),
								MsgType = (msg.IsWithdrawn ? "system" : msg.MsgType),
								SendTime = msg.SendTime,
								IsWithdrawn = msg.IsWithdrawn,
								IsSelf = (msg.SenderName == _myNickName),
								QuoteContent = msg.QuoteContent,
								QuoteSender = msg.QuoteSender,
								Avatar = msg.Avatar
							});
						}
						if (msg.Id > num2)
						{
							num2 = msg.Id;
						}
						if (!msg.IsWithdrawn && msg.Id > lastReadId && msg.SenderName != _myNickName && IsMentioned(msg.Content))
						{
							num++;
						}
					}
					room2.CurrentSkip += history.Count;
					room2.HasMoreHistory = history.Count == 50;
				}
				room2.IsInitialLoaded = true;
				if (room2 == SelectedGroup)
				{
					SaveLastReadId(num2);
					this.InitialHistoryLoaded?.Invoke();
					if (num > 0)
					{
						Growl.Warning($"\ud83d\udd14 您不在的时候，有 {num} 条新消息 @ 了您！", "ChatRoomGrowl");
					}
				}
			});
		}
		catch (Exception ex2)
		{
			Exception ex = ex2;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				Growl.Error("⚠\ufe0f 历史记录拉取失败: " + ex.Message, "ChatRoomGrowl");
			});
		}
	}

	public async Task LoadMoreHistoryAsync()
	{
		ChatRoomModel room = SelectedGroup;
		if (room == null || !room.HasMoreHistory)
		{
			return;
		}
		if (_connection == null || _connection.State != HubConnectionState.Connected)
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				Growl.Warning("网络已断开，无法加载历史记录", "ChatRoomGrowl");
			});
			return;
		}
		try
		{
			List<ServerChatMessage> history = await _connection.InvokeAsync<List<ServerChatMessage>>("GetHistory", room.Name, room.CurrentSkip, 50);
			if (history.Count == 0)
			{
				room.HasMoreHistory = false;
				return;
			}
			Application.Current?.Dispatcher.Invoke(delegate
			{
				for (int num = history.Count - 1; num >= 0; num--)
				{
					ServerChatMessage msg = history[num];
					if (!room.Messages.Any((ChatMessageModel m) => m.Id == msg.Id && msg.Id > 0))
					{
						room.Messages.Insert(0, new ChatMessageModel
						{
							Id = msg.Id,
							SenderName = msg.SenderName,
							SenderIp = msg.SenderIp,
							Content = (msg.IsWithdrawn ? "\ud83d\udeab [该消息已被撤回]" : msg.Content),
							MsgType = (msg.IsWithdrawn ? "system" : msg.MsgType),
							SendTime = msg.SendTime,
							IsWithdrawn = msg.IsWithdrawn,
							IsSelf = (msg.SenderName == _myNickName),
							QuoteContent = msg.QuoteContent,
							QuoteSender = msg.QuoteSender,
							Avatar = msg.Avatar
						});
					}
				}
				room.CurrentSkip += history.Count;
				if (history.Count < 50)
				{
					room.HasMoreHistory = false;
				}
			});
		}
		catch (Exception ex2)
		{
			Exception ex = ex2;
			if (ex is InvalidOperationException || ex.Message.Contains("not active"))
			{
				Application.Current?.Dispatcher.Invoke(delegate
				{
					Growl.Info("网络发生波动，获取历史记录失败，请稍后再试", "ChatRoomGrowl");
				});
			}
			else
			{
				Application.Current?.Dispatcher.Invoke(delegate
				{
					Growl.Error("⚠\ufe0f 加载更多记录失败: " + ex.Message, "ChatRoomGrowl");
				});
			}
		}
	}

	public async Task SendDirectImageAsync(string base64)
	{
		string base65 = base64;
		if (_connection.State != HubConnectionState.Connected)
		{
			return;
		}
		IsUploadingImage = true;
		try
		{
			await Task.Run(async delegate
			{
				using HttpClient client = new HttpClient
				{
					Timeout = TimeSpan.FromSeconds(30.0)
				};
				string requestUri = ChatServiceConfig.BuildUrl("/api/upload/image");
				byte[] content2 = Convert.FromBase64String(base65);
				using MultipartFormDataContent content = new MultipartFormDataContent();
				ByteArrayContent byteArrayContent = new ByteArrayContent(content2);
				byteArrayContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
				content.Add(byteArrayContent, "file", "chat_img.png");
				HttpResponseMessage httpResponseMessage = await client.PostAsync(requestUri, content);
				if (httpResponseMessage.IsSuccessStatusCode)
				{
					using JsonDocument doc = JsonDocument.Parse(await httpResponseMessage.Content.ReadAsStringAsync());
					string text = "";
					JsonElement value2;
					if (doc.RootElement.TryGetProperty("url", out var value))
					{
						text = value.GetString();
					}
					else if (doc.RootElement.TryGetProperty("Url", out value2))
					{
						text = value2.GetString();
					}
					if (!string.IsNullOrEmpty(text))
					{
						await _connection.InvokeAsync("SendMessage", _myNickName, SelectedGroup.Name, "image", text, null);
					}
				}
			});
		}
		catch (Exception ex2)
		{
			Exception ex = ex2;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				Growl.Error("图片发送失败: " + ex.Message);
			});
		}
		finally
		{
			Application.Current?.Dispatcher.Invoke(() => IsUploadingImage = false);
		}
	}

	public async Task UpdateAvatarAsync(string avatarCode)
	{
		if (_connection.State != HubConnectionState.Connected)
		{
			return;
		}
		try
		{
			await _connection.InvokeAsync("UpdateAvatar", _myNickName, avatarCode);
		}
		catch (Exception ex)
		{
			Growl.Error("发送失败: " + ex.Message, "ChatRoomGrowl");
		}
	}

	public async Task DisconnectAsync()
	{
		if (_connection != null)
		{
			try
			{
				await _connection.StopAsync();
				await _connection.DisposeAsync();
			}
			catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
		}
	}

	private bool IsMentioned(string content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return false;
		}
		string text = "@" + _myNickName;
		int length = text.Length;
		int startIndex = 0;
		while ((startIndex = content.IndexOf(text, startIndex, StringComparison.OrdinalIgnoreCase)) != -1)
		{
			int num = startIndex + length;
			if (num >= content.Length)
			{
				return true;
			}
			char c = content[num];
			if (!char.IsLetterOrDigit(c) && c != '_' && (c < '一' || c > '龥'))
			{
				return true;
			}
			startIndex = num;
		}
		return false;
	}

	private int GetLastReadId()
	{
		try
		{
			string path = Path.Combine(Path.GetTempPath(), "AIHelper_chat_" + _myNickName + ".txt");
			if (File.Exists(path) && int.TryParse(File.ReadAllText(path), out var result))
			{
				return result;
			}
		}
		catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
		return 0;
	}

	private void SaveLastReadId(int id)
	{
		try
		{
			File.WriteAllText(Path.Combine(Path.GetTempPath(), "AIHelper_chat_" + _myNickName + ".txt"), id.ToString());
		}
		catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
	}

	public void Dispose()
	{
		_ = _connection?.DisposeAsync();
	}

	
}
