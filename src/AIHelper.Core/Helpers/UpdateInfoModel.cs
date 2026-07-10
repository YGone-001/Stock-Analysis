using System.Text.Json.Serialization;

#pragma warning disable CS8618
#pragma warning disable CS8618
namespace AIHelper.Helpers;

public class UpdateInfoModel
{
	[JsonPropertyName("version")]
	public string Version { get; set; } = null!;

	[JsonPropertyName("url")]
	public string Url { get; set; } = null!;

	[JsonPropertyName("description")]
	public string Description { get; set; } = null!;
}
