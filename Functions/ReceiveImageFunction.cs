using AudioToTranscript.Configuration;
using AudioToTranscript.Services;
using AudioToTranscript.Utils;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace AudioToTranscript.Functions;

/// <summary>
/// Synchronous image transcript endpoint.
/// POST /api/image  — multipart/form-data with part name="image"
/// Returns 200 { text, confidence } immediately. No queue, no Salesforce, no email.
/// </summary>
public class ReceiveImageFunction
{
    private readonly ITranscriptionService _transcription;
    private readonly PipelineOptions _options;
    private readonly ILogger<ReceiveImageFunction> _logger;

    public ReceiveImageFunction(
        ITranscriptionService transcription,
        IOptions<PipelineOptions> options,
        ILogger<ReceiveImageFunction> logger)
    {
        _transcription = transcription;
        _options       = options.Value;
        _logger        = logger;
    }

    [Function("ReceiveImage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "image")] HttpRequestData req)
    {
        var contentType = req.Headers.TryGetValues("Content-Type", out var ctValues)
            ? ctValues.First() : "";

        if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return await JsonResponse(req, HttpStatusCode.BadRequest,
                new { error = "Content-Type must be multipart/form-data." });
        }

        byte[] imageBytes;
        string fileName;
        try
        {
            (imageBytes, fileName) = await MultipartParser.ParseImageAsync(req.Body, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse image multipart request.");
            return await JsonResponse(req, HttpStatusCode.BadRequest,
                new { error = $"Invalid multipart body: {ex.Message}" });
        }

        if (imageBytes.Length == 0)
        {
            return await JsonResponse(req, HttpStatusCode.BadRequest,
                new { error = "Image part is empty." });
        }

        // Detect content type from file extension
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var mediaContentType = ext switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png"           => "image/png",
            "gif"           => "image/gif",
            "webp"          => "image/webp",
            _               => "image/jpeg"
        };

        _logger.LogInformation("ReceiveImage: {File} ({Bytes} bytes) as {CT}", fileName, imageBytes.Length, mediaContentType);

        try
        {
            var result = await RetryHelper.ExecuteAsync(
                "ImageTranscription",
                () => _transcription.TranscribeAsync(imageBytes, mediaContentType),
                _options.TranscriptionMaxRetries,
                _options.TranscriptionRetryBaseMs,
                _logger,
                shouldRetry: ex => ex is HttpRequestException or TaskCanceledException);

            return await JsonResponse(req, HttpStatusCode.OK, new
            {
                text       = result.Text,
                confidence = result.Confidence
            });
        }
        catch (StageException ex)
        {
            _logger.LogError(ex, "Image transcription failed after {Attempts} attempts.", ex.Attempts);
            return await JsonResponse(req, HttpStatusCode.BadGateway, new
            {
                error    = $"Transcription failed after {ex.Attempts} attempts.",
                detail   = ex.InnerException?.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Image transcription rejected.");
            return await JsonResponse(req, HttpStatusCode.UnprocessableEntity,
                new { error = ex.Message });
        }
    }

    private static async Task<HttpResponseData> JsonResponse(HttpRequestData req, HttpStatusCode status, object body)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body));
        return response;
    }
}
