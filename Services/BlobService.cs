using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AudioToTranscript.Services;

public class BlobService : IBlobService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobService> _logger;

    public BlobService(IConfiguration config, ILogger<BlobService> logger)
    {
        _logger = logger;
        var connStr       = config.GetValue<string>("AzureWebJobsStorage")!;
        var containerName = config.GetValue<string>("BLOB_CONTAINER_MEDIA") ?? "media";
        _container = new BlobContainerClient(connStr, containerName);
        _container.CreateIfNotExists(PublicAccessType.None);
    }

    public async Task<string> UploadMediaAsync(byte[] mediaBytes, string blobPath, string contentType)
    {
        var client = _container.GetBlobClient(blobPath);
        using var stream = new MemoryStream(mediaBytes);
        await client.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } });
        _logger.LogInformation("Uploaded blob {Path} ({Bytes} bytes)", blobPath, mediaBytes.Length);
        return client.Uri.ToString();
    }

    public async Task<byte[]> DownloadMediaAsync(string blobPath)
    {
        var client = _container.GetBlobClient(blobPath);
        var download = await client.DownloadContentAsync();
        var bytes = download.Value.Content.ToArray();
        _logger.LogInformation("Downloaded blob {Path} ({Bytes} bytes)", blobPath, bytes.Length);
        return bytes;
    }
}
