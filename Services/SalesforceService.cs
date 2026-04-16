using AudioToTranscript.Configuration;
using AudioToTranscript.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioToTranscript.Services;

public class SalesforceService : ISalesforceService
{
    private readonly HttpClient _http;
    private readonly SalesforceOptions _options;
    private readonly ILogger<SalesforceService> _logger;

    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public SalesforceService(HttpClient http, IOptions<SalesforceOptions> options, ILogger<SalesforceService> logger)
    {
        _http    = http;
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<string> CreateCaseAsync(AudioMetadata metadata, string transcriptionText)
    {
        await EnsureTokenAsync();

        var payload = new
        {
            BrandId__c            = metadata.BrandId,
            Status                = "New",
            CaseOrigin__c         = "IVR",
            OwnerId               = _options.DefaultOwnerId,
            RecordTypeId__c       = _options.RecordTypeId,
            Description           = $"IVR notified caller of delivery date and delivery plan date. Here's what they said: {transcriptionText}",
            Phone                 = metadata.Phone,
            CallType__c           = metadata.CallTypeMapped,
            CSTproblemReported__c = metadata.CstProblemReported
        };

        var url = $"{_options.InstanceUrl.TrimEnd('/')}/services/data/v52.0/sobjects/Case/";
        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        _logger.LogInformation("Posting Case to Salesforce. CaseId={CaseId}", metadata.CaseId);
        var response = await _http.SendAsync(request);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Token expired — refresh once and retry
            _logger.LogWarning("Salesforce 401 — refreshing token and retrying.");
            await RefreshTokenAsync();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _accessToken) }
            });
        }

        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Salesforce API returned {Status}: {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"Salesforce API error {(int)response.StatusCode}: {body}",
                null, response.StatusCode);
        }

        var sfResponse = JsonSerializer.Deserialize<SalesforceCreateResponse>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var caseUrl = $"{_options.InstanceUrl.TrimEnd('/')}/{sfResponse?.Id}";
        _logger.LogInformation("Salesforce Case created. Id={Id} Url={Url}", sfResponse?.Id, caseUrl);
        return $"201 / {caseUrl}";
    }

    private async Task EnsureTokenAsync()
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry) return;
        await RefreshTokenAsync();
    }

    private async Task RefreshTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry) return;

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type",    "client_credentials"),
                new KeyValuePair<string, string>("client_id",     _options.ClientId),
                new KeyValuePair<string, string>("client_secret", _options.ClientSecret)
            });

            var response = await _http.PostAsync(_options.TokenUrl, form);
            var body     = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Salesforce token request failed {(int)response.StatusCode}: {body}");

            var token = JsonSerializer.Deserialize<SalesforceTokenResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            _accessToken = token.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn - 60 : 3540);
            _logger.LogInformation("Salesforce token refreshed. ExpiresIn={Sec}s", token.ExpiresIn);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed class SalesforceTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("expires_in")]   public int    ExpiresIn   { get; set; }
    }

    private sealed class SalesforceCreateResponse
    {
        [JsonPropertyName("id")]      public string Id      { get; set; } = "";
        [JsonPropertyName("success")] public bool   Success { get; set; }
    }
}
