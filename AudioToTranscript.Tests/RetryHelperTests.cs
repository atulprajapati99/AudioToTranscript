using AudioToTranscript.Utils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudioToTranscript.Tests;

public class RetryHelperTests
{
    [Fact]
    public async Task ExecuteAsync_SucceedsFirstAttempt_ReturnsResult()
    {
        var result = await RetryHelper.ExecuteAsync(
            "Test", () => Task.FromResult(42), 3, 1, NullLogger.Instance);

        result.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteAsync_FailsThenSucceeds_ReturnsResultAfterRetry()
    {
        int callCount = 0;
        var result = await RetryHelper.ExecuteAsync("Test", () =>
        {
            callCount++;
            if (callCount < 3) throw new HttpRequestException("transient");
            return Task.FromResult(99);
        }, maxAttempts: 3, baseDelayMs: 1, NullLogger.Instance);

        result.Should().Be(99);
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_AllAttemptsFail_ThrowsStageException()
    {
        int callCount = 0;
        var act = async () => await RetryHelper.ExecuteAsync("Transcription", () =>
        {
            callCount++;
            throw new HttpRequestException("server error");
#pragma warning disable CS0162
            return Task.FromResult(0);
#pragma warning restore CS0162
        }, maxAttempts: 3, baseDelayMs: 1, NullLogger.Instance);

        await act.Should().ThrowAsync<StageException>()
            .WithMessage("*Transcription*3 attempt*");

        callCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientError_DoesNotRetry()
    {
        int callCount = 0;
        var act = async () => await RetryHelper.ExecuteAsync("Test", () =>
        {
            callCount++;
            throw new ArgumentException("bad input");
#pragma warning disable CS0162
            return Task.FromResult(0);
#pragma warning restore CS0162
        }, maxAttempts: 3, baseDelayMs: 1, NullLogger.Instance);

        await act.Should().ThrowAsync<StageException>();
        callCount.Should().Be(1); // no retry for non-transient
    }

    [Fact]
    public async Task ExecuteAsync_CustomShouldRetry_UsesCustomPredicate()
    {
        int callCount = 0;
        var act = async () => await RetryHelper.ExecuteAsync("Test", () =>
        {
            callCount++;
            throw new InvalidOperationException("custom retryable");
#pragma warning disable CS0162
            return Task.FromResult(0);
#pragma warning restore CS0162
        },
        maxAttempts: 3, baseDelayMs: 1, NullLogger.Instance,
        shouldRetry: ex => ex is InvalidOperationException);

        await act.Should().ThrowAsync<StageException>();
        callCount.Should().Be(3); // retried because custom predicate returned true
    }
}
