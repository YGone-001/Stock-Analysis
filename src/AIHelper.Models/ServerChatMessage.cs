using System;

namespace AIHelper.Models;

public class ServerChatMessage
{
	public int Id { get; set; }

	public string SenderName { get; set; }

	public string SenderIp { get; set; }

	public string GroupName { get; set; }

	public string MsgType { get; set; }

	public string Content { get; set; }

	public bool IsWithdrawn { get; set; }

	public int? QuoteId { get; set; }

	public DateTime SendTime { get; set; }

	public string QuoteContent { get; set; }

	public string QuoteSender { get; set; }

	public string Avatar { get; set; }
}
