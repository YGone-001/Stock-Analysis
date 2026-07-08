using System.Text.Json.Serialization;

namespace AIHelper.Helpers;

public class UpdateInfoModel
{
	[JsonPropertyName("version")]
	public string Version { get; set; }

	[JsonPropertyName("url")]
	public string Url { get; set; }

	[JsonPropertyName("description")]
	public string Description { get; set; }
}
