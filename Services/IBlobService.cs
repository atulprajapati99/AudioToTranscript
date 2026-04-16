namespace AudioToTranscript.Services;

public interface IBlobService
{
    Task<string> UploadMediaAsync(byte[] mediaBytes, string blobPath, string contentType);
    Task<byte[]> DownloadMediaAsync(string blobPath);
}
