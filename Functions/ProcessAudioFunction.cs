using AudioToTranscript.Configuration;
using AudioToTranscript.Models;
using AudioToTranscript.Services;
using AudioToTranscript.Utils;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AudioToTranscript.Functions;

public class ProcessAudioFunction
{
    private readonly ITranscriptionService _transcription;
    private readonly ISalesforceService _salesforce;
    private readonly IBlobService _blob;
    private readonly IAuditService _audit;
    private readonly IEmailService _email;
    private readonly PipelineOptions _options;
    private readonly ILogger<ProcessAudioFunction> _logger;

    public ProcessAudioFunction(
        ITranscriptionService transcription,
        ISalesforceService salesforce,
        IBlobService blob,
        IAuditService audit,
        IEmailService email,
        IOptions<PipelineOptions> options,
        ILogger<ProcessAudioFunction> logger)
    {
        _transcription = transcription;
        _salesforce    = salesforce;
        _blob          = blob;
        _audit         = audit;
        _email         = email;
        _options       = options.Value;
        _logger        = logger;
    }

    [Function("ProcessAudio")]
    public async Task Run(
        [QueueTrigger("%QUEUE_NAME%", Connection = "AzureWebJobsStorage")] string messageJson)
    {
        ProcessingMessage? message;
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(messageJson));
            message = JsonSerializer.Deserialize<ProcessingMessage>(decoded,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize queue message. Dropping to avoid DLQ loop.");
            return; // Poison message — do not rethrow
        }

        if (message?.Metadata == null)
        {
            _logger.LogError("Queue message had null metadata. Dropping.");
            return;
        }

        var metadata = message.Metadata;
        var result   = new ProcessingResult();

        _logger.LogInformation("Processing CaseId={CaseId} CallType={CT}", metadata.CaseId, metadata.CallTypeRaw);

        // ── Step 1: Download audio ────────────────────────────────────────────
        byte[] mediaBytes = Array.Empty<byte>();
        if (_options.EnableBlobStorage && !string.IsNullOrEmpty(metadata.BlobPath))
        {
            try
            {
                mediaBytes = await _blob.DownloadMediaAsync(metadata.BlobPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download blob {Path}", metadata.BlobPath);
                // Continue — transcription will fail gracefully if bytes are empty
            }
        }

        // ── Step 2: Transcription ─────────────────────────────────────────────
        if (_options.EnableTranscription)
        {
            bool isImage = metadata.BlobPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || metadata.BlobPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                        || metadata.BlobPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var mediaContentType = isImage ? "image/jpeg" : "audio/wav";

            int transcriptionAttempts = 0;
            try
            {
                var transcriptionResponse = await RetryHelper.ExecuteAsync(
                    "Transcription",
                    async () =>
                    {
                        transcriptionAttempts++;
                        return await _transcription.TranscribeAsync(mediaBytes, mediaContentType);
                    },
                    _options.TranscriptionMaxRetries,
                    _options.TranscriptionRetryBaseMs,
                    _logger,
                    shouldRetry: ex => ex is HttpRequestException or TaskCanceledException);

                result.TranscriptionText     = transcriptionResponse.Text;
                result.TranscriptionStatus   = "Success";
                result.TranscriptionAttempts = transcriptionAttempts;

                await _audit.UpdateTranscriptionAsync(
                    metadata.CaseId, metadata.DatePartition, "Success", transcriptionAttempts);
            }
            catch (StageException ex)
            {
                result.TranscriptionStatus   = "Failed";
                result.TranscriptionAttempts = ex.Attempts;
                result.TranscriptionError    = ex.InnerException?.Message ?? ex.Message;
                result.FinalStatus           = "Failed";
                result.FailedAtStage         = "Transcription";

                await _audit.UpdateTranscriptionAsync(
                    metadata.CaseId, metadata.DatePartition, "Failed", ex.Attempts, result.TranscriptionError);

                // Send failure email with WAV attached
                if (_options.EnableEmail && _options.EmailOnFailure)
                {
                    var (sent, emailError) = await _email.SendFailureEmailAsync(
                        metadata, "Transcription", ex.Attempts, result.TranscriptionError!,
                        attachWavBytes: mediaBytes.Length > 0 ? mediaBytes : null);

                    result.EmailType  = "TranscriptionFailed";
                    result.EmailSent  = sent;
                    result.EmailError = emailError;

                    await _audit.UpdateEmailAsync(
                        metadata.CaseId, metadata.DatePartition, "TranscriptionFailed", sent, emailError);
                }

                await _audit.UpdateFinalStatusAsync(
                    metadata.CaseId, metadata.DatePartition, "Failed", "Transcription");

                // Rethrow so queue retries (up to maxDequeueCount) then DLQ
                throw;
            }
        }

        // ── Step 3: Salesforce Case ───────────────────────────────────────────
        if (_options.EnableSalesforce)
        {
            int sfAttempts = 0;
            try
            {
                var sfResponse = await RetryHelper.ExecuteAsync(
                    "Salesforce",
                    async () =>
                    {
                        sfAttempts++;
                        return await _salesforce.CreateCaseAsync(metadata, result.TranscriptionText);
                    },
                    _options.SalesforceMaxRetries,
                    _options.SalesforceRetryBaseMs,
                    _logger,
                    shouldRetry: ex => ex is HttpRequestException or TaskCanceledException);

                result.SalesforceStatus   = "Success";
                result.SalesforceAttempts = sfAttempts;
                result.SalesforceResponse = sfResponse;

                await _audit.UpdateSalesforceAsync(
                    metadata.CaseId, metadata.DatePartition, "Success", sfAttempts, sfResponse);
            }
            catch (StageException ex)
            {
                result.SalesforceStatus   = "Failed";
                result.SalesforceAttempts = ex.Attempts;
                result.SalesforceError    = ex.InnerException?.Message ?? ex.Message;
                result.FinalStatus        = "Failed";
                result.FailedAtStage      = "Salesforce";

                await _audit.UpdateSalesforceAsync(
                    metadata.CaseId, metadata.DatePartition, "Failed", ex.Attempts,
                    error: result.SalesforceError);

                // Send failure email — include transcription text so team can create case manually
                if (_options.EnableEmail && _options.EmailOnFailure)
                {
                    var (sent, emailError) = await _email.SendFailureEmailAsync(
                        metadata, "Salesforce", ex.Attempts, result.SalesforceError!,
                        transcriptionText: result.TranscriptionText);

                    result.EmailType  = "SalesforceFailed";
                    result.EmailSent  = sent;
                    result.EmailError = emailError;

                    await _audit.UpdateEmailAsync(
                        metadata.CaseId, metadata.DatePartition, "SalesforceFailed", sent, emailError);
                }

                await _audit.UpdateFinalStatusAsync(
                    metadata.CaseId, metadata.DatePartition, "Failed", "Salesforce");

                throw;
            }
        }

        // ── Step 4: Success email ─────────────────────────────────────────────
        if (_options.EnableEmail && _options.EmailOnSuccess)
        {
            var (sent, emailError) = await _email.SendSuccessEmailAsync(
                metadata, result.TranscriptionText, result.SalesforceResponse ?? "");

            result.EmailType  = "Success";
            result.EmailSent  = sent;
            result.EmailError = emailError;

            await _audit.UpdateEmailAsync(
                metadata.CaseId, metadata.DatePartition, "Success", sent, emailError);
        }

        result.FinalStatus = "Success";
        await _audit.UpdateFinalStatusAsync(metadata.CaseId, metadata.DatePartition, "Success");

        _logger.LogInformation("CaseId={CaseId} processed successfully.", metadata.CaseId);
    }
}
