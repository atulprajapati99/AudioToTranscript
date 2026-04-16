using AudioToTranscript.Models;

namespace AudioToTranscript.Services;

public interface IEmailService
{
    Task<(bool Sent, string? Error)> SendSuccessEmailAsync(AudioMetadata metadata, string transcriptionText, string salesforceResponse);
    Task<(bool Sent, string? Error)> SendFailureEmailAsync(AudioMetadata metadata, string failedStage, int attempts, string errorMessage, byte[]? attachWavBytes = null, string? transcriptionText = null);
}
