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

namespace AIHelper.Models;

public class ChatMessageModel : INotifyPropertyChanged
{
	private string _avatar;

	private string _content;

	private bool _isWithdrawn;

	private ImageSource _loadedImage;

	private bool _isDownloading;

	private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
	{
		ServerCertificateCustomValidationCallback = (HttpRequestMessage message, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors) => true
	})
	{
		Timeout = TimeSpan.FromSeconds(30.0)
	};

	public int Id { get; set; }

	public string SenderName { get; set; }

	public string SenderIp { get; set; }

	public string MsgType { get; set; }

	public DateTime SendTime { get; set; }

	public bool IsSelf { get; set; }

	public string QuoteContent { get; set; }

	public string QuoteSender { get; set; }

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
			OnPropertyChanged("Avatar");
			OnPropertyChanged("HasLocalAvatar");
			OnPropertyChanged("LocalAvatarPath");
		}
	}

	public bool HasLocalAvatar => !string.IsNullOrEmpty(Avatar);

	public string LocalAvatarPath
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
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(1, 2);
			defaultInterpolatedStringHandler.AppendFormatted(SenderName);
			defaultInterpolatedStringHandler.AppendLiteral(" ");
			defaultInterpolatedStringHandler.AppendFormatted(SendTime, "HH:mm:ss");
			return defaultInterpolatedStringHandler.ToStringAndClear();
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
				OnPropertyChanged("Content");
				OnPropertyChanged("ImageSource");
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
			OnPropertyChanged("IsWithdrawn");
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
			OnPropertyChanged("IsDownloading");
		}
	}

	public ImageSource ImageSource
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

	public event PropertyChangedEventHandler PropertyChanged;

	private void PrepareImageWithSafeCache()
	{
		byte[] imageBytes;
		Task.Run(async delegate
		{
			_ = 2;
			try
			{
				imageBytes = null;
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
						catch
						{
						}
					}
					if (imageBytes == null || imageBytes.Length == 0)
					{
						Application.Current.Dispatcher.Invoke(() => IsDownloading = true);
						imageBytes = await _httpClient.GetByteArrayAsync(fullUrl);
						try
						{
							await File.WriteAllBytesAsync(localFilePath, imageBytes);
						}
						catch
						{
						}
					}
				}
				else
				{
					imageBytes = Convert.FromBase64String(Content);
				}
				if (imageBytes != null && imageBytes.Length != 0)
				{
					Application.Current.Dispatcher.Invoke(delegate
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
							OnPropertyChanged("ImageSource");
						}
						catch
						{
						}
					});
				}
			}
			catch (Exception)
			{
			}
			finally
			{
				Application.Current.Dispatcher.Invoke(() => IsDownloading = false);
			}
		});
	}

	protected void OnPropertyChanged([CallerMemberName] string name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
