#nullable enable
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIHelper.Models;

namespace AIHelper.ViewModels;

public class ChatRoomModel : INotifyPropertyChanged
{
	private int _unreadCount;

	private bool _isSelected;

	private string _name = string.Empty;

	public string Name
	{
		get => _name;
		set
		{
			if (_name != value)
			{
				_name = value;
				OnPropertyChanged(nameof(Name));
			}
		}
	}

	public int UnreadCount
	{
		get
		{
			return _unreadCount;
		}
		set
		{
			_unreadCount = value;
			OnPropertyChanged(nameof(UnreadCount));
		}
	}

	public bool IsSelected
	{
		get
		{
			return _isSelected;
		}
		set
		{
			_isSelected = value;
			OnPropertyChanged(nameof(IsSelected));
		}
	}

	public ObservableCollection<ChatMessageModel> Messages { get; set; } = new ObservableCollection<ChatMessageModel>();


	public bool IsInitialLoaded { get; set; }

	public int CurrentSkip { get; set; }

	public bool HasMoreHistory { get; set; } = true;


	public event PropertyChangedEventHandler? PropertyChanged = null;

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
