using AudioToTranscript.Configuration;
using AudioToTranscript.Models;
using AudioToTranscript.Services;
using AudioToTranscript.Utils;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace AudioToTranscript.Functions;

public class ReceiveAudioFunction
{
    private readonly IBlobService _blob;
    private readonly IAuditService _audit;
    private readonly ICallTypeMapper _mapper;
    private readonly PipelineOptions _options;
    private readonly QueueClient _queue;
    private readonly ILogger<ReceiveAudioFunction> _logger;

    public ReceiveAudioFunction(
        IBlobService blob,
        IAuditService audit,
        ICallTypeMapper mapper,
        IOptions<PipelineOptions> options,
        IConfiguration config,
        ILogger<ReceiveAudioFunction> logger)
    {
        _blob    = blob;
        _audit   = audit;
        _mapper  = mapper;
        _options = options.Value;
        _logger  = logger;

        var connStr   = config.GetValue<string>("AzureWebJobsStorage")!;
        var queueName = config.GetValue<string>("QUEUE_NAME") ?? "audio-processing-queue";
        _queue = new QueueClient(connStr, queueName);
    }

    [Function("ReceiveAudio")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "audio")] HttpRequestData req)
    {
        _logger.LogInformation("ReceiveAudio triggered. ContentType={CT}", req.Headers.TryGetValues("Content-Type", out var ct) ? string.Join(",", ct) : "none");

        var contentType = req.Headers.TryGetValues("Content-Type", out var ctValues)
            ? ctValues.First() : "";

        // Validate Content-Type
        if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Rejected request — Content-Type must be multipart/form-data, got: {CT}", contentType);
            return await BadRequest(req, "Content-Type must be multipart/form-data.");
        }

        // Parse multipart
        AudioMetadata metadata;
        byte[] mediaBytes;
        string fileName;
        try
        {
            (metadata, mediaBytes, fileName) = await MultipartParser.ParseAudioAsync(req.Body, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse multipart request.");
            return await BadRequest(req, $"Invalid multipart body: {ex.Message}");
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(metadata.CaseId) || string.IsNullOrWhiteSpace(metadata.Phone))
        {
            return await BadRequest(req, "Metadata must include non-empty 'caseId' and 'phone'.");
        }

        // Map call type
        var (callTypeMapped, cstProblem) = _mapper.Map(metadata.CallTypeRaw);
        metadata.CallTypeMapped     = callTypeMapped;
        metadata.CstProblemReported = cstProblem;

        // Build blob path: media/YYYY-MM-DD/{caseId}_{timestamp}.wav
        var date     = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var ext      = Path.GetExtension(fileName).TrimStart('.').ToLower();
        if (string.IsNullOrEmpty(ext)) ext = "wav";
        metadata.BlobPath = $"media/{date}/{metadata.CaseId}_{metadata.Timestamp}.{ext}";

        // Upload to Blob Storage
        if (_options.EnableBlobStorage)
        {
            try
            {
                var detectedContentType = ext == "wav" ? "audio/wav" : $"audio/{ext}";
                await _blob.UploadMediaAsync(mediaBytes, metadata.BlobPath, detectedContentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload blob for CaseId={CaseId}", metadata.CaseId);
                return await ErrorResponse(req, "Failed to store audio file. Please retry.");
            }
        }

        // Write initial audit row
        try
        {
            await _audit.CreateInitialRowAsync(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write initial audit row for CaseId={CaseId}", metadata.CaseId);
            // Non-fatal — continue to enqueue
        }

        // Enqueue processing message
        var message = new ProcessingMessage { BlobPath = metadata.BlobPath, Metadata = metadata };
        var messageJson = JsonSerializer.Serialize(message);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson));
        await _queue.CreateIfNotExistsAsync();
        await _queue.SendMessageAsync(encoded);

        _logger.LogInformation("CaseId={CaseId} queued for processing. BlobPath={Path}", metadata.CaseId, metadata.BlobPath);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            id     = metadata.CaseId,
            status = "queued"
        }));
        return response;
    }

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string message)
    {
        var r = req.CreateResponse(HttpStatusCode.BadRequest);
        r.Headers.Add("Content-Type", "application/json");
        await r.WriteStringAsync(JsonSerializer.Serialize(new { error = message }));
        return r;
    }

    private static async Task<HttpResponseData> ErrorResponse(HttpRequestData req, string message)
    {
        var r = req.CreateResponse(HttpStatusCode.InternalServerError);
        r.Headers.Add("Content-Type", "application/json");
        await r.WriteStringAsync(JsonSerializer.Serialize(new { error = message }));
        return r;
    }
}
