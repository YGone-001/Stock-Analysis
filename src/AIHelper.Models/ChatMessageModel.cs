#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHelper.Helpers;
using Serilog;

namespace AIHelper.Models;

public class ChatMessageModel : INotifyPropertyChanged
{
	private string _avatar = string.Empty;

	private string _content = string.Empty;


	private bool _isWithdrawn;

	private ImageSource? _loadedImage;

	private bool _isDownloading;

	private static readonly HttpClient _httpClient = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(30.0)
	};

	public int Id { get; set; }

	public string SenderName { get; set; } = string.Empty;

	public string SenderIp { get; set; } = string.Empty;

	public string MsgType { get; set; } = string.Empty;

	public DateTime SendTime { get; set; }

	public bool IsSelf { get; set; }

	public string QuoteContent { get; set; } = string.Empty;

	public string QuoteSender { get; set; } = string.Empty;

	public bool HasQuote => !string.IsNullOrEmpty(QuoteContent);

	public string Avatar
	{
		get
		{
			return _avatar;
		}
		set
		{
			_avatar = value;
			OnPropertyChanged(nameof(Avatar));
			OnPropertyChanged(nameof(HasLocalAvatar));
			OnPropertyChanged(nameof(LocalAvatarPath));
		}
	}

	public bool HasLocalAvatar => !string.IsNullOrEmpty(Avatar);

	public string? LocalAvatarPath
	{
		get
		{
			if (!HasLocalAvatar)
			{
				return null;
			}
			return "pack://application:,,,/Assets/face/" + Avatar + ".png";
		}
	}

	public string HeaderText
	{
		get
		{
			return $"{SenderName} {SendTime:HH:mm:ss}";
		}
	}

	public bool IsText => MsgType == "text";

	public bool IsImage => MsgType == "image";

	public bool IsSystem => MsgType == "system";

	public string Content
	{
		get
		{
			return _content;
		}
		set
		{
			if (_content != value)
			{
				_content = value;
				OnPropertyChanged(nameof(Content));
				OnPropertyChanged(nameof(ImageSource));
			}
		}
	}

	public bool IsWithdrawn
	{
		get
		{
			return _isWithdrawn;
		}
		set
		{
			_isWithdrawn = value;
			OnPropertyChanged(nameof(IsWithdrawn));
		}
	}

	public bool IsDownloading
	{
		get
		{
			return _isDownloading;
		}
		set
		{
			_isDownloading = value;
			OnPropertyChanged(nameof(IsDownloading));
		}
	}

	public ImageSource? ImageSource
	{
		get
		{
			if (string.IsNullOrEmpty(Content) || !IsImage)
			{
				return null;
			}
			if (_loadedImage == null && !_isDownloading)
			{
				PrepareImageWithSafeCache();
			}
			return _loadedImage;
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged = null;

	private void PrepareImageWithSafeCache()
	{
		byte[]? imageBytes = null;
		Task.Run(async delegate
		{
			_ = 2;
			try
			{
				if (Content.StartsWith("/uploads") || Content.StartsWith("http"))
				{
					string fullUrl = Content.StartsWith("http") ? Content : ChatServiceConfig.BuildUrl(Content);
					string text = Path.GetFileName(new Uri(fullUrl).LocalPath);
					if (string.IsNullOrEmpty(text))
					{
						text = Guid.NewGuid().ToString("N") + ".png";
					}
					string text2 = Path.Combine(Path.GetTempPath(), "AIHelper_ChatImages");
					if (!Directory.Exists(text2))
					{
						Directory.CreateDirectory(text2);
					}
					string localFilePath = Path.Combine(text2, text);
					if (File.Exists(localFilePath))
					{
						try
						{
							using FileStream fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
							using MemoryStream ms = new MemoryStream();
							await fs.CopyToAsync(ms);
							imageBytes = ms.ToArray();
						}
						catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
					}
					if (imageBytes == null || imageBytes.Length == 0)
					{
						Application.Current?.Dispatcher.Invoke(() => IsDownloading = true);
						imageBytes = await _httpClient.GetByteArrayAsync(fullUrl);
						try
						{
							await File.WriteAllBytesAsync(localFilePath, imageBytes);
						}
						catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
					}
				}
				else
				{
					imageBytes = Convert.FromBase64String(Content);
				}
				if (imageBytes != null && imageBytes.Length != 0)
				{
					Application.Current?.Dispatcher.Invoke(delegate
					{
						try
						{
							BitmapImage bitmapImage = new BitmapImage();
							using (MemoryStream streamSource = new MemoryStream(imageBytes))
							{
								bitmapImage.BeginInit();
								bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
								bitmapImage.StreamSource = streamSource;
								bitmapImage.EndInit();
							}
							bitmapImage.Freeze();
							_loadedImage = bitmapImage;
							OnPropertyChanged(nameof(ImageSource));
						}
						catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
					});
				}
			}
			catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
			finally
			{
				Application.Current?.Dispatcher.Invoke(() => IsDownloading = false);
			}
		});
	}

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
