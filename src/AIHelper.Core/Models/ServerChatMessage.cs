using System;

#pragma warning disable CS8618
#pragma warning disable CS8618
namespace AIHelper.Models;

public class ServerChatMessage
{
	public int Id { get; set; }

	public string SenderName { get; set; } = null!;

	public string SenderIp { get; set; } = null!;

	public string GroupName { get; set; } = null!;

	public string MsgType { get; set; } = null!;

	public string Content { get; set; } = null!;

	public bool IsWithdrawn { get; set; }

	public int? QuoteId { get; set; }

	public DateTime SendTime { get; set; }

	public string QuoteContent { get; set; } = null!;

	public string QuoteSender { get; set; } = null!;

	public string Avatar { get; set; } = null!;
}
