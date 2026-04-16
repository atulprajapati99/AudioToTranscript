using AudioToTranscript.Utils;
using FluentAssertions;
using System.Text;
using Xunit;

namespace AudioToTranscript.Tests;

public class MultipartParserTests
{
    private const string Boundary = "testboundary123";

    private static (Stream body, string contentType) BuildMultipart(
        string metadataJson, byte[] audioBytes, string audioPartName = "audio", string fileName = "test.wav")
    {
        var ct = $"multipart/form-data; boundary={Boundary}";
        var sb = new StringBuilder();
        sb.Append($"--{Boundary}\r\n");
        sb.Append("Content-Disposition: form-data; name=\"metadata\"\r\n");
        sb.Append("Content-Type: application/json\r\n\r\n");
        sb.Append(metadataJson);
        sb.Append($"\r\n--{Boundary}\r\n");
        sb.Append($"Content-Disposition: form-data; name=\"{audioPartName}\"; filename=\"{fileName}\"\r\n\r\n");

        var header = Encoding.UTF8.GetBytes(sb.ToString());
        var footer = Encoding.UTF8.GetBytes($"\r\n--{Boundary}--\r\n");

        var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(audioBytes);
        ms.Write(footer);
        ms.Position = 0;
        return (ms, ct);
    }

    [Fact]
    public async Task ParseAudioAsync_ValidRequest_ExtractsMetadataAndBytes()
    {
        var json = """{"callType":"D|FR","caseId":"0017V00001","phone":"9794920458","timestamp":"12345","brandId":"4600"}""";
        var audioBytes = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // RIFF header

        var (body, ct) = BuildMultipart(json, audioBytes);
        var (metadata, bytes, fileName) = await MultipartParser.ParseAudioAsync(body, ct);

        metadata.CallTypeRaw.Should().Be("D|FR");
        metadata.CaseId.Should().Be("0017V00001");
        metadata.Phone.Should().Be("9794920458");
        metadata.Timestamp.Should().Be("12345");
        metadata.BrandId.Should().Be("4600");
        bytes.Should().BeEquivalentTo(audioBytes);
        fileName.Should().Be("test.wav");
    }

    [Fact]
    public async Task ParseAudioAsync_MissingMetadataPart_ThrowsInvalidOperation()
    {
        // Build a multipart with no metadata part (just audio)
        var ct = $"multipart/form-data; boundary={Boundary}";
        var content = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"audio\"; filename=\"x.wav\"\r\n\r\ndata\r\n--{Boundary}--\r\n";
        var body = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var act = async () => await MultipartParser.ParseAudioAsync(body, ct);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*metadata*");
    }

    [Fact]
    public async Task ParseAudioAsync_MissingAudioPart_ThrowsInvalidOperation()
    {
        var json = """{"callType":"D|FR","caseId":"001","phone":"123","timestamp":"1","brandId":"1"}""";
        var ct = $"multipart/form-data; boundary={Boundary}";
        var content = $"--{Boundary}\r\nContent-Disposition: form-data; name=\"metadata\"\r\nContent-Type: application/json\r\n\r\n{json}\r\n--{Boundary}--\r\n";
        var body = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var act = async () => await MultipartParser.ParseAudioAsync(body, ct);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*audio*");
    }

    [Fact]
    public async Task ParseImageAsync_ValidRequest_ReturnsBytes()
    {
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF }; // JPEG magic bytes
        var (body, ct) = BuildMultipart("{}", imageBytes, audioPartName: "image", fileName: "invoice.jpg");

        // Need a proper image-only multipart
        var imgCt = $"multipart/form-data; boundary={Boundary}";
        var sb = new StringBuilder();
        sb.Append($"--{Boundary}\r\n");
        sb.Append("Content-Disposition: form-data; name=\"image\"; filename=\"invoice.jpg\"\r\n\r\n");
        var header = Encoding.UTF8.GetBytes(sb.ToString());
        var footer = Encoding.UTF8.GetBytes($"\r\n--{Boundary}--\r\n");
        var ms = new MemoryStream();
        ms.Write(header); ms.Write(imageBytes); ms.Write(footer);
        ms.Position = 0;

        var (bytes, fileName) = await MultipartParser.ParseImageAsync(ms, imgCt);
        bytes.Should().BeEquivalentTo(imageBytes);
        fileName.Should().Be("invoice.jpg");
    }
}
