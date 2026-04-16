namespace AudioToTranscript.Models;

public class AudioMetadata
{
    public string CallTypeRaw        { get; set; } = "";
    public string CallTypeMapped     { get; set; } = "";
    public string CstProblemReported { get; set; } = "";
    public string CaseId             { get; set; } = "";
    public string Phone              { get; set; } = "";
    public string Timestamp          { get; set; } = "";
    public string BrandId            { get; set; } = "";
    public string BlobPath           { get; set; } = "";
    public string ReceivedAt         { get; set; } = DateTime.UtcNow.ToString("O");

    // Convenience: Table Storage PartitionKey (YYYY-MM-DD)
    public string DatePartition => ReceivedAt.Length >= 10 ? ReceivedAt[..10] : DateTime.UtcNow.ToString("yyyy-MM-dd");
}
