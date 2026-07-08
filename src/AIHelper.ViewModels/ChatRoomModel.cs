#nullable enable
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AIHelper.Models;

namespace AIHelper.ViewModels;

public partial class ChatRoomModel : ObservableObject
{
	[ObservableProperty]
	private int _unreadCount;

	[ObservableProperty]
	private bool _isSelected;

	[ObservableProperty]
	private string _name = string.Empty;

	public ObservableCollection<ChatMessageModel> Messages { get; set; } = new ObservableCollection<ChatMessageModel>();

	public bool IsInitialLoaded { get; set; }

	public int CurrentSkip { get; set; }

	public bool HasMoreHistory { get; set; } = true;
}
