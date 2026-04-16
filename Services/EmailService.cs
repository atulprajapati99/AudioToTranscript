using AudioToTranscript.Configuration;
using AudioToTranscript.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AudioToTranscript.Services;

public class EmailService : IEmailService
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<EmailOptions> options, ILogger<EmailService> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<(bool Sent, string? Error)> SendSuccessEmailAsync(
        AudioMetadata metadata, string transcriptionText, string salesforceResponse)
    {
        var subject = $"[SUCCESS] {metadata.CallTypeRaw} | CaseId: {metadata.CaseId} | Phone: {metadata.Phone}";
        var body = $"""
            Call successfully processed.

            Call Type:        {metadata.CallTypeMapped} — {metadata.CstProblemReported}
            Case ID:          {metadata.CaseId}
            Phone:            {metadata.Phone}
            Received At:      {metadata.ReceivedAt}
            Salesforce Case:  {salesforceResponse}

            Transcription:
            {transcriptionText}
            """;

        return await SendAsync(subject, body);
    }

    public async Task<(bool Sent, string? Error)> SendFailureEmailAsync(
        AudioMetadata metadata, string failedStage, int attempts, string errorMessage,
        byte[]? attachWavBytes = null, string? transcriptionText = null)
    {
        var subject = $"[FAILED] Stage: {failedStage} | {metadata.CallTypeRaw} | CaseId: {metadata.CaseId} | Phone: {metadata.Phone}";
        var body = new System.Text.StringBuilder();
        body.AppendLine($"Processing FAILED at stage: {failedStage}");
        body.AppendLine();
        body.AppendLine($"Attempts:    {attempts} of {attempts}");
        body.AppendLine($"Last error:  {errorMessage}");
        body.AppendLine();
        body.AppendLine($"Call Type:   {metadata.CallTypeMapped} — {metadata.CstProblemReported}");
        body.AppendLine($"Case ID:     {metadata.CaseId}");
        body.AppendLine($"Phone:       {metadata.Phone}");
        body.AppendLine($"Received At: {metadata.ReceivedAt}");
        body.AppendLine($"Audio Blob:  {metadata.BlobPath}");

        if (!string.IsNullOrEmpty(transcriptionText))
        {
            body.AppendLine();
            body.AppendLine("Transcription (succeeded before Salesforce failure):");
            body.AppendLine(transcriptionText);
            body.AppendLine("(Manual Salesforce case creation may be required.)");
        }

        return await SendAsync(subject, body.ToString(), attachWavBytes, $"{metadata.CaseId}.wav");
    }

    private async Task<(bool Sent, string? Error)> SendAsync(
        string subject, string bodyText, byte[]? attachment = null, string attachmentName = "")
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(_options.Username));
            foreach (var recipient in _options.RecipientList)
                message.To.Add(MailboxAddress.Parse(recipient));
            message.Subject = subject;

            var builder = new BodyBuilder { TextBody = bodyText };
            if (attachment != null && attachment.Length > 0)
                builder.Attachments.Add(attachmentName, attachment, ContentType.Parse("audio/wav"));

            message.Body = builder.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_options.SmtpHost, _options.SmtpPort, SecureSocketOptions.StartTls);
            await smtp.AuthenticateAsync(_options.Username, _options.Password);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(quit: true);

            _logger.LogInformation("Email sent. Subject={Subject}", subject);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email. Subject={Subject}", subject);
            return (false, ex.Message);
        }
    }
}
