namespace AudioToTranscript.Models;

public class ProcessingMessage
{
    public string        BlobPath { get; set; } = "";
    public AudioMetadata Metadata { get; set; } = new();
}
