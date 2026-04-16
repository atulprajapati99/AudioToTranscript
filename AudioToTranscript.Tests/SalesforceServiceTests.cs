using AudioToTranscript.Configuration;
using AudioToTranscript.Models;
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

public class SalesforceServiceTests
{
    private static readonly SalesforceOptions DefaultOptions = new()
    {
        ClientId     = "client-id",
        ClientSecret = "client-secret",
        TokenUrl     = "https://fake-sf.test/token",
        InstanceUrl  = "https://fake-sf.test",
        RecordTypeId = "01217U000000GjgQAG"
    };

    private static AudioMetadata SampleMetadata() => new()
    {
        CaseId             = "0017V000001",
        Phone              = "9794920458",
        CallTypeRaw        = "D|FR",
        CallTypeMapped     = "Delivery / Order Related",
        CstProblemReported = "Fill Request",
        BrandId            = "4600"
    };

    private static HttpResponseMessage TokenOk() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { access_token = "token123", expires_in = 3600 }),
                Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage CaseCreated() =>
        new(HttpStatusCode.Created)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { id = "500XX0000001", success = true }),
                Encoding.UTF8, "application/json")
        };

    private static ISalesforceService BuildService(
        IReadOnlyList<HttpResponseMessage> responses,
        SalesforceOptions? options = null)
    {
        int callIndex = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => responses[Math.Min(callIndex++, responses.Count - 1)]);

        return new SalesforceService(
            new HttpClient(handler.Object),
            Options.Create(options ?? DefaultOptions),
            NullLogger<SalesforceService>.Instance);
    }

    [Fact]
    public async Task CreateCaseAsync_HappyPath_ReturnsCaseUrl()
    {
        var svc = BuildService(new[] { TokenOk(), CaseCreated() });
        var result = await svc.CreateCaseAsync(SampleMetadata(), "Test transcription");

        result.Should().Contain("201");
        result.Should().Contain("500XX0000001");
    }

    [Fact]
    public async Task CreateCaseAsync_500Response_ThrowsHttpRequestException()
    {
        var svc = BuildService(new[]
        {
            TokenOk(),
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
                { Content = new StringContent("server error") }
        });

        var act = async () => await svc.CreateCaseAsync(SampleMetadata(), "text");
        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*500*");
    }

    [Fact]
    public async Task CreateCaseAsync_401ThenSuccess_RefreshesTokenAndSucceeds()
    {
        var svc = BuildService(new[]
        {
            TokenOk(),                                                          // initial token
            new HttpResponseMessage(HttpStatusCode.Unauthorized),               // first case POST → 401
            TokenOk(),                                                          // token refresh (not called here — handled inside service)
            CaseCreated()                                                       // retry case POST
        });

        // 401 path triggers internal token refresh + immediate retry
        var result = await svc.CreateCaseAsync(SampleMetadata(), "text");
        result.Should().Contain("201");
    }
}
