using Microsoft.Extensions.Logging;

namespace AudioToTranscript.Utils;

public static class RetryHelper
{
    /// <summary>
    /// Executes <paramref name="action"/> up to <paramref name="maxAttempts"/> times
    /// with exponential backoff. Logs every attempt to App Insights.
    /// Only retries on transient errors (network, 5xx) unless a custom
    /// <paramref name="shouldRetry"/> predicate is provided.
    /// Throws <see cref="StageException"/> after all attempts are exhausted.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        string stageName,
        Func<Task<T>> action,
        int maxAttempts,
        int baseDelayMs,
        ILogger logger,
        Func<Exception, bool>? shouldRetry = null)
    {
        Exception? lastEx = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await action();
                sw.Stop();
                logger.LogInformation(
                    "{Stage} succeeded on attempt {Attempt}/{Max} in {Ms}ms",
                    stageName, attempt, maxAttempts, sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                lastEx = ex;
                bool retryable = shouldRetry?.Invoke(ex) ?? IsTransient(ex);

                if (attempt < maxAttempts && retryable)
                {
                    var delayMs = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                    logger.LogWarning(ex,
                        "{Stage} attempt {Attempt}/{Max} failed in {Ms}ms — retrying in {Delay}ms. Error: {Error}",
                        stageName, attempt, maxAttempts, sw.ElapsedMilliseconds, delayMs, ex.Message);
                    await Task.Delay(delayMs);
                }
                else
                {
                    logger.LogError(ex,
                        "{Stage} attempt {Attempt}/{Max} failed in {Ms}ms — {Reason}. Error: {Error}",
                        stageName, attempt, maxAttempts, sw.ElapsedMilliseconds,
                        retryable ? "max attempts reached" : "non-retryable error",
                        ex.Message);
                    break;
                }
            }
        }

        throw new StageException(stageName, maxAttempts, lastEx!);
    }

    /// <summary>Overload for actions with no return value.</summary>
    public static Task ExecuteAsync(
        string stageName,
        Func<Task> action,
        int maxAttempts,
        int baseDelayMs,
        ILogger logger,
        Func<Exception, bool>? shouldRetry = null) =>
        ExecuteAsync(
            stageName,
            async () => { await action(); return true; },
            maxAttempts, baseDelayMs, logger, shouldRetry);

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;
}
