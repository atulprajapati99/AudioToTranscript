using AudioToTranscript.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AudioToTranscript.Utils;

public interface ICallTypeMapper
{
    (string CallTypeC, string CstProblemReportedC) Map(string callTypeCode);
}

public class CallTypeMapper : ICallTypeMapper
{
    private readonly Dictionary<string, CallTypeEntry> _mappings;
    private readonly ILogger<CallTypeMapper> _logger;

    public CallTypeMapper(IConfiguration configuration, ILogger<CallTypeMapper> logger)
    {
        _logger = logger;
        _mappings = configuration
            .GetSection("CallTypeMappings")
            .Get<Dictionary<string, CallTypeEntry>>() ?? new();
    }

    public (string CallTypeC, string CstProblemReportedC) Map(string callTypeCode)
    {
        if (_mappings.TryGetValue(callTypeCode, out var entry))
            return (entry.CallType, entry.ProblemType);

        _logger.LogWarning("Unknown callType code '{Code}' — defaulting to Unknown. Add it to CallTypeMappings.json.", callTypeCode);
        return ("Unknown", "");
    }
}
