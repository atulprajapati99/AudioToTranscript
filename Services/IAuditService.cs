using AudioToTranscript.Models;

namespace AudioToTranscript.Services;

public interface IAuditService
{
    Task CreateInitialRowAsync(AudioMetadata metadata);
    Task UpdateTranscriptionAsync(string caseId, string datePartition, string status, int attempts, string? error = null);
    Task UpdateSalesforceAsync(string caseId, string datePartition, string status, int attempts, string? response = null, string? error = null);
    Task UpdateEmailAsync(string caseId, string datePartition, string emailType, bool sent, string? emailError = null);
    Task UpdateFinalStatusAsync(string caseId, string datePartition, string status, string? failedAtStage = null);
}
