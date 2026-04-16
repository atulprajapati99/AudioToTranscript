namespace AudioToTranscript.Configuration;

public class EmailOptions
{
    public string SmtpHost   { get; set; } = "";
    public int    SmtpPort   { get; set; } = 587;
    public string Username   { get; set; } = "";
    public string Password   { get; set; } = "";
    public string Recipients { get; set; } = "";

    public string[] RecipientList =>
        Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
