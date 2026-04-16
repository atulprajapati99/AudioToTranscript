namespace AudioToTranscript.Configuration;

public class SalesforceOptions
{
    public string ClientId       { get; set; } = "";
    public string ClientSecret   { get; set; } = "";
    public string TokenUrl       { get; set; } = "";
    public string InstanceUrl    { get; set; } = "";
    public string RecordTypeId   { get; set; } = "01217U000000GjgQAG";
    public string DefaultOwnerId { get; set; } = "";
}
