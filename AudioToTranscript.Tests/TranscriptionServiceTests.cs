using AudioToTranscript.Configuration;
using AudioToTranscript.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AudioToTranscript.Tests;

public class TranscriptionServiceTests
{
    private static ITranscriptionService BuildService(
        HttpResponseMessage response,
        PipelineOptions? options = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var http = new HttpClient(handler.Object);
        var opts = Options.Create(options ?? new PipelineOptions
        {
            TranscriptionEndpoint = "https://fake-transcription.test/transcribe",
            TranscriptionApiKey   = "test-key"
        });
        return new TranscriptionService(http, opts, NullLogger<TranscriptionService>.Instance);
    }

    private static HttpResponseMessage Ok(string text, double? confidence = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { text, confidence }),
                Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task TranscribeAsync_Success_ReturnsText()
    {
        var svc = BuildService(Ok("Hello caller", 0.95));
        var result = await svc.TranscribeAsync(new byte[] { 1, 2, 3 }, "audio/wav");

        result.Text.Should().Be("Hello caller");
        result.Confidence.Should().Be(0.95);
    }

    [Fact]
    public async Task TranscribeAsync_500Response_ThrowsHttpRequestException()
    {
        var svc = BuildService(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server error")
        });

        var act = async () => await svc.TranscribeAsync(new byte[] { 1 }, "audio/wav");
        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*500*");
    }

    [Fact]
    public async Task TranscribeAsync_400Response_ThrowsInvalidOperation_NotRetryable()
    {
        var svc = BuildService(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad audio")
        });

        var act = async () => await svc.TranscribeAsync(new byte[] { 1 }, "audio/wav");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*400*");
    }

    [Fact]
    public async Task TranscribeAsync_BelowConfidenceThreshold_Throws()
    {
        var options = new PipelineOptions
        {
            TranscriptionEndpoint        = "https://fake.test/t",
            MinTranscriptionConfidence   = 0.8
        };
        var svc = BuildService(Ok("low confidence text", 0.5), options);

        var act = async () => await svc.TranscribeAsync(new byte[] { 1 }, "audio/wav");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*confidence*below*");
    }

    [Fact]
    public async Task TranscribeAsync_NoConfidenceThreshold_AcceptsAnyScore()
    {
        var options = new PipelineOptions
        {
            TranscriptionEndpoint      = "https://fake.test/t",
            MinTranscriptionConfidence = 0.0  // disabled
        };
        var svc = BuildService(Ok("text", 0.1), options);

        var result = await svc.TranscribeAsync(new byte[] { 1 }, "audio/wav");
        result.Text.Should().Be("text");
    }

    [Fact]
    public async Task TranscribeAsync_MissingEndpoint_ThrowsInvalidOperation()
    {
        var svc = BuildService(Ok("x"), new PipelineOptions { TranscriptionEndpoint = "" });
        var act = async () => await svc.TranscribeAsync(new byte[] { 1 }, "audio/wav");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*TranscriptionEndpoint*not configured*");
    }
}
