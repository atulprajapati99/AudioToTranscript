using AudioToTranscript.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AudioToTranscript.Utils;

public static class MultipartParser
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Parses a multipart/form-data request body into an AudioMetadata and raw media bytes.
    /// Expects two parts:
    ///   name="metadata"  — JSON with callType, caseId, phone, timestamp, brandId
    ///   name="audio"     — raw WAV bytes  (or name="image" for image requests)
    /// </summary>
    public static async Task<(AudioMetadata Metadata, byte[] MediaBytes, string FileName)> ParseAudioAsync(
        Stream body, string contentType)
    {
        var boundary = GetBoundary(contentType);
        var reader = new MultipartReader(boundary, body);

        AudioMetadata? metadata = null;
        byte[]? mediaBytes = null;
        string fileName = "";

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync()) != null)
        {
            var disposition = section.GetContentDispositionHeader();
            if (disposition == null) continue;

            var name = disposition.Name.Value?.Trim('"') ?? "";

            if (name.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            {
                var json = await new StreamReader(section.Body, Encoding.UTF8).ReadToEndAsync();
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions) ?? new();

                metadata = new AudioMetadata
                {
                    CallTypeRaw = raw.GetValueOrDefault("callType", ""),
                    CaseId      = raw.GetValueOrDefault("caseId", ""),
                    Phone       = raw.GetValueOrDefault("phone", ""),
                    Timestamp   = raw.GetValueOrDefault("timestamp", ""),
                    BrandId     = raw.GetValueOrDefault("brandId", ""),
                    ReceivedAt  = DateTime.UtcNow.ToString("O")
                };
            }
            else if (name.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                fileName = disposition.FileName.Value?.Trim('"') ?? "";
                using var ms = new MemoryStream();
                await section.Body.CopyToAsync(ms);
                mediaBytes = ms.ToArray();
            }
        }

        if (metadata == null)
            throw new InvalidOperationException("Multipart request is missing the 'metadata' part.");
        if (mediaBytes == null || mediaBytes.Length == 0)
            throw new InvalidOperationException("Multipart request is missing the audio/image part or it is empty.");

        return (metadata, mediaBytes, fileName);
    }

    /// <summary>
    /// Simpler parse for image-only requests: returns raw image bytes + filename.
    /// </summary>
    public static async Task<(byte[] ImageBytes, string FileName)> ParseImageAsync(
        Stream body, string contentType)
    {
        var boundary = GetBoundary(contentType);
        var reader = new MultipartReader(boundary, body);

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync()) != null)
        {
            var disposition = section.GetContentDispositionHeader();
            if (disposition == null) continue;

            var name = disposition.Name.Value?.Trim('"') ?? "";
            if (name.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = disposition.FileName.Value?.Trim('"') ?? "";
                using var ms = new MemoryStream();
                await section.Body.CopyToAsync(ms);
                return (ms.ToArray(), fileName);
            }
        }

        throw new InvalidOperationException("Multipart request is missing the 'image' part.");
    }

    private static string GetBoundary(string contentType)
    {
        var parsed = MediaTypeHeaderValue.Parse(contentType);
        var boundary = HeaderUtilities.RemoveQuotes(parsed.Boundary).Value
            ?? throw new InvalidOperationException("Missing boundary in Content-Type header.");
        return boundary;
    }
}
