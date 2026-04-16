using AudioToTranscript.Configuration;
using AudioToTranscript.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AudioToTranscript.Services;

public class TranscriptionService : ITranscriptionService
{
    private readonly HttpClient _http;
    private readonly PipelineOptions _options;
    private readonly ILogger<TranscriptionService> _logger;

    public TranscriptionService(HttpClient http, IOptions<PipelineOptions> options, ILogger<TranscriptionService> logger)
    {
        _http    = http;
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<TranscriptionResponse> TranscribeAsync(byte[] mediaBytes, string contentType)
    {
        if (string.IsNullOrWhiteSpace(_options.TranscriptionEndpoint))
            throw new InvalidOperationException("Pipeline:TranscriptionEndpoint is not configured.");

        using var content = new ByteArrayContent(mediaBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TranscriptionEndpoint)
        {
            Content = content
        };

        if (!string.IsNullOrWhiteSpace(_options.TranscriptionApiKey))
            request.Headers.Add("Authorization", _options.TranscriptionApiKey);

        _logger.LogInformation("Calling transcription API. Bytes={Bytes} ContentType={ContentType}",
            mediaBytes.Length, contentType);

        var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Transcription API returned {Status}: {Body}", (int)response.StatusCode, body);

            // 400/415 are not retryable — throw immediately without retry wrapper catching it
            if ((int)response.StatusCode is 400 or 415)
                throw new InvalidOperationException(
                    $"Transcription API rejected request ({(int)response.StatusCode}): {body}");

            // 5xx / other: throw HttpRequestException so RetryHelper retries
            throw new HttpRequestException(
                $"Transcription API error {(int)response.StatusCode}: {body}",
                null, response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<TranscriptionResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Transcription API returned empty or unparseable response.");

        if (_options.MinTranscriptionConfidence > 0.0
            && result.Confidence.HasValue
            && result.Confidence.Value < _options.MinTranscriptionConfidence)
        {
            throw new InvalidOperationException(
                $"Transcription confidence {result.Confidence:F2} is below minimum {_options.MinTranscriptionConfidence:F2}.");
        }

        _logger.LogInformation("Transcription succeeded. Confidence={Confidence} TextLength={Len}",
            result.Confidence, result.Text.Length);

        return result;
    }
}
