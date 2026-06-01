namespace AudioToTranscript.Utils;

public class AudioFileTooLargeException : Exception
{
    public AudioFileTooLargeException(string message) : base(message) { }
}
