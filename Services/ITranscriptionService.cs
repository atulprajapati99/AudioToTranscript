using AudioToTranscript.Models;

namespace AudioToTranscript.Services;

public interface ITranscriptionService
{
    Task<TranscriptionResponse> TranscribeAsync(byte[] mediaBytes, string contentType);
}
