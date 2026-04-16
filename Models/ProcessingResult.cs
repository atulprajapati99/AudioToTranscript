namespace AudioToTranscript.Models;

public class ProcessingResult
{
    public string  TranscriptionText     { get; set; } = "";
    public string  TranscriptionStatus   { get; set; } = "Pending";
    public int     TranscriptionAttempts { get; set; }
    public string? TranscriptionError    { get; set; }

    public string  SalesforceStatus   { get; set; } = "Skipped";
    public int     SalesforceAttempts { get; set; }
    public string? SalesforceResponse { get; set; }
    public string? SalesforceError    { get; set; }

    public string  EmailType  { get; set; } = "";
    public bool    EmailSent  { get; set; }
    public string? EmailError { get; set; }

    public string  FinalStatus    { get; set; } = "Pending";
    public string? FailedAtStage  { get; set; }
}
