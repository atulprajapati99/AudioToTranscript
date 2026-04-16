using System.Text.Json.Serialization;

namespace AudioToTranscript.Models;

public class TranscriptionResponse
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}
