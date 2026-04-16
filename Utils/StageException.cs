namespace AudioToTranscript.Utils;

public class StageException : Exception
{
    public string StageName { get; }
    public int    Attempts  { get; }

    public StageException(string stageName, int attempts, Exception innerException)
        : base($"{stageName} failed after {attempts} attempt(s): {innerException.Message}", innerException)
    {
        StageName = stageName;
        Attempts  = attempts;
    }
}
