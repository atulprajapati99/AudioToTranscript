using AudioToTranscript.Models;
using AudioToTranscript.Utils;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioToTranscript.Tests;

public class CallTypeMapperTests
{
    private static ICallTypeMapper BuildMapper(Dictionary<string, CallTypeEntry> entries)
    {
        var dict = entries.ToDictionary(
            kv => $"CallTypeMappings:{kv.Key}:CallType",
            kv => kv.Value.CallType)
            .Concat(entries.ToDictionary(
                kv => $"CallTypeMappings:{kv.Key}:ProblemType",
                kv => kv.Value.ProblemType))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(dict!)
            .Build();
        return new CallTypeMapper(config, NullLogger<CallTypeMapper>.Instance);
    }

    [Fact]
    public void Map_KnownCode_ReturnsCorrectValues()
    {
        var mapper = BuildMapper(new()
        {
            ["D|FR"] = new CallTypeEntry { CallType = "Delivery / Order Related", ProblemType = "Fill Request" }
        });

        var (callType, problem) = mapper.Map("D|FR");

        callType.Should().Be("Delivery / Order Related");
        problem.Should().Be("Fill Request");
    }

    [Fact]
    public void Map_FeedbackCode_ReturnsCorrectValues()
    {
        var mapper = BuildMapper(new()
        {
            ["FeedBack|CallCenterFeedback"] = new CallTypeEntry
            {
                CallType    = "Feedback on Bottler Employee",
                ProblemType = "Call Center Feedback"
            }
        });

        var (callType, problem) = mapper.Map("FeedBack|CallCenterFeedback");

        callType.Should().Be("Feedback on Bottler Employee");
        problem.Should().Be("Call Center Feedback");
    }

    [Fact]
    public void Map_UnknownCode_ReturnsUnknownWithoutThrowing()
    {
        var mapper = BuildMapper(new());

        var act = () => mapper.Map("NONEXISTENT|CODE");
        act.Should().NotThrow();

        var (callType, problem) = mapper.Map("NONEXISTENT|CODE");
        callType.Should().Be("Unknown");
        problem.Should().Be("");
    }

    [Fact]
    public void Map_EmptyCode_ReturnsUnknown()
    {
        var mapper = BuildMapper(new());
        var (callType, _) = mapper.Map("");
        callType.Should().Be("Unknown");
    }
}
