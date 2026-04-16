using AudioToTranscript.Models;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace AudioToTranscript.Functions;

/// <summary>
/// Re-queues a previously failed audio file for reprocessing without re-uploading.
/// POST /api/replay  — body: { "blobPath": "media/2026-04-16/0017V...wav", "caseId": "...", "callTypeRaw": "...", "phone": "..." }
/// </summary>
public class ReplayFunction
{
    private readonly QueueClient _queue;
    private readonly ILogger<ReplayFunction> _logger;

    public ReplayFunction(IConfiguration config, ILogger<ReplayFunction> logger)
    {
        _logger = logger;
        var connStr   = config.GetValue<string>("AzureWebJobsStorage")!;
        var queueName = config.GetValue<string>("QUEUE_NAME") ?? "audio-processing-queue";
        _queue = new QueueClient(connStr, queueName);
        _queue.CreateIfNotExists();
    }

    [Function("Replay")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "replay")] HttpRequestData req)
    {
        ReplayRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ReplayRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return await JsonResponse(req, HttpStatusCode.BadRequest,
                new { error = "Request body must be valid JSON with blobPath, caseId, callTypeRaw, phone." });
        }

        if (body == null || string.IsNullOrWhiteSpace(body.BlobPath) || string.IsNullOrWhiteSpace(body.CaseId))
        {
            return await JsonResponse(req, HttpStatusCode.BadRequest,
                new { error = "blobPath and caseId are required." });
        }

        var metadata = new AudioMetadata
        {
            CaseId         = body.CaseId,
            CallTypeRaw    = body.CallTypeRaw ?? "",
            CallTypeMapped = body.CallTypeMapped ?? "",
            Phone          = body.Phone ?? "",
            Timestamp      = body.Timestamp ?? "",
            BrandId        = body.BrandId ?? "",
            BlobPath       = body.BlobPath,
            ReceivedAt     = DateTime.UtcNow.ToString("O")
        };

        var message        = new ProcessingMessage { BlobPath = body.BlobPath, Metadata = metadata };
        var messageJson    = JsonSerializer.Serialize(message);
        var encoded        = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson));

        await _queue.SendMessageAsync(encoded);

        _logger.LogInformation("Replay queued for CaseId={CaseId} BlobPath={Path}", body.CaseId, body.BlobPath);

        return await JsonResponse(req, HttpStatusCode.Accepted,
            new { id = body.CaseId, status = "queued-for-replay", blobPath = body.BlobPath });
    }

    private static async Task<HttpResponseData> JsonResponse(HttpRequestData req, HttpStatusCode status, object body)
    {
        var r = req.CreateResponse(status);
        r.Headers.Add("Content-Type", "application/json");
        await r.WriteStringAsync(JsonSerializer.Serialize(body));
        return r;
    }

    private sealed class ReplayRequest
    {
        public string? BlobPath      { get; set; }
        public string? CaseId        { get; set; }
        public string? CallTypeRaw   { get; set; }
        public string? CallTypeMapped{ get; set; }
        public string? Phone         { get; set; }
        public string? Timestamp     { get; set; }
        public string? BrandId       { get; set; }
    }
}
