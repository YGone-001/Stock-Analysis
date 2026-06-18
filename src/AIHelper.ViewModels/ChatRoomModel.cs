using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIHelper.Models;

namespace AIHelper.ViewModels;

public class ChatRoomModel : INotifyPropertyChanged
{
	private int _unreadCount;

	private bool _isSelected;

	public string Name { get; set; }

	public int UnreadCount
	{
		get
		{
			return _unreadCount;
		}
		set
		{
			_unreadCount = value;
			OnPropertyChanged("UnreadCount");
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
			OnPropertyChanged("IsSelected");
		}
	}

	public ObservableCollection<ChatMessageModel> Messages { get; set; } = new ObservableCollection<ChatMessageModel>();


	public bool IsInitialLoaded { get; set; }

	public int CurrentSkip { get; set; }

	public bool HasMoreHistory { get; set; } = true;


	public event PropertyChangedEventHandler PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
