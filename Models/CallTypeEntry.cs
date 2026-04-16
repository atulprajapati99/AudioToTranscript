namespace AudioToTranscript.Models;

public class CallTypeEntry
{
    // Property names match the keys in CallTypeMappings.json (IConfiguration binding is case-insensitive)
    public string CallType    { get; set; } = "";
    public string ProblemType { get; set; } = "";
}
