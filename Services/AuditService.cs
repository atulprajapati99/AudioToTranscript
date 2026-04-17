using AudioToTranscript.Models;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AudioToTranscript.Services;

public class AuditService : IAuditService
{
    private readonly TableClient _table;
    private readonly ILogger<AuditService> _logger;
    private readonly int _retentionDays;

    public AuditService(IConfiguration config, ILogger<AuditService> logger)
    {
        _logger = logger;
        _retentionDays = config.GetValue<int>("Pipeline:AuditRetentionDays", 30);
        var connStr   = config.GetValue<string>("AzureWebJobsStorage")!;
        var tableName = config.GetValue<string>("TABLE_AUDIT_LOG") ?? "AudioProcessingLog";
        _table = new TableClient(connStr, tableName);
    }

    public async Task CreateInitialRowAsync(AudioMetadata metadata)
    {
        await _table.CreateIfNotExistsAsync();
        var retainUntil = DateTime.UtcNow.AddDays(_retentionDays).ToString("yyyy-MM-dd");
        var entity = new TableEntity(metadata.DatePartition, metadata.CaseId)
        {
            ["CallTypeRaw"]    = metadata.CallTypeRaw,
            ["CallTypeMapped"] = metadata.CallTypeMapped,
            ["CstProblem"]     = metadata.CstProblemReported,
            ["Phone"]          = metadata.Phone,
            ["BlobPath"]       = metadata.BlobPath,
            ["ReceivedAt"]     = metadata.ReceivedAt,
            ["FinalStatus"]    = "Received",
            ["RetainUntil"]    = retainUntil
        };
        await _table.UpsertEntityAsync(entity);
        _logger.LogInformation("Audit row created for CaseId={CaseId}", metadata.CaseId);
    }

    public async Task UpdateTranscriptionAsync(string caseId, string datePartition, string status, int attempts, string? error = null)
    {
        var entity = new TableEntity(datePartition, caseId)
        {
            ["TranscriptionStatus"]   = status,
            ["TranscriptionAttempts"] = attempts,
            ["TranscriptionError"]    = error ?? ""
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Merge);
        _logger.LogInformation("Audit Transcription={Status} Attempts={Attempts} CaseId={CaseId}", status, attempts, caseId);
    }

    public async Task UpdateSalesforceAsync(string caseId, string datePartition, string status, int attempts, string? response = null, string? error = null)
    {
        var entity = new TableEntity(datePartition, caseId)
        {
            ["SalesforceStatus"]   = status,
            ["SalesforceAttempts"] = attempts,
            ["SalesforceResponse"] = response ?? "",
            ["SalesforceError"]    = error ?? ""
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Merge);
        _logger.LogInformation("Audit Salesforce={Status} Attempts={Attempts} CaseId={CaseId}", status, attempts, caseId);
    }

    public async Task UpdateEmailAsync(string caseId, string datePartition, string emailType, bool sent, string? emailError = null)
    {
        var entity = new TableEntity(datePartition, caseId)
        {
            ["EmailType"]  = emailType,
            ["EmailSent"]  = sent.ToString(),
            ["EmailError"] = emailError ?? ""
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Merge);
        _logger.LogInformation("Audit Email type={Type} sent={Sent} CaseId={CaseId}", emailType, sent, caseId);
    }

    public async Task UpdateFinalStatusAsync(string caseId, string datePartition, string status, string? failedAtStage = null)
    {
        var entity = new TableEntity(datePartition, caseId)
        {
            ["FinalStatus"]   = status,
            ["FailedAtStage"] = failedAtStage ?? ""
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Merge);
        _logger.LogInformation("Audit FinalStatus={Status} FailedAtStage={Stage} CaseId={CaseId}", status, failedAtStage, caseId);
    }
}
