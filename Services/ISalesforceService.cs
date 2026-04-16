using AudioToTranscript.Models;

namespace AudioToTranscript.Services;

public interface ISalesforceService
{
    Task<string> CreateCaseAsync(AudioMetadata metadata, string transcriptionText);
}
