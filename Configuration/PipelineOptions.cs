namespace AudioToTranscript.Configuration;

public class PipelineOptions
{
    public bool   EnableBlobStorage           { get; set; } = true;
    public bool   EnableTranscription         { get; set; } = true;
    public bool   EnableSalesforce            { get; set; } = true;
    public bool   EnableEmail                 { get; set; } = true;
    public bool   EmailOnSuccess              { get; set; } = true;
    public bool   EmailOnFailure              { get; set; } = true;

    public string TranscriptionEndpoint       { get; set; } = "";
    public string TranscriptionApiKey         { get; set; } = "";
    public int    TranscriptionMaxRetries     { get; set; } = 3;
    public int    TranscriptionRetryBaseMs    { get; set; } = 2000;
    public double MinTranscriptionConfidence  { get; set; } = 0.0;

    public int    SalesforceMaxRetries        { get; set; } = 3;
    public int    SalesforceRetryBaseMs       { get; set; } = 2000;

    public int    AuditRetentionDays          { get; set; } = 30;
}
