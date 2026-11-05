using System.Diagnostics;

namespace IdempotencyGuard.Stress.Tests.Support;

/// <summary>
/// Result of a load run: every operation's return value, plus timing.
/// </summary>
public sealed record LoadResult<T>(
    IReadOnlyList<T> Results,
    LatencyStats Latency,
    TimeSpan Elapsed,
    int Failures)
{
    public int Operations => Results.Count;

    public double OperationsPerSecond =>
        Elapsed.TotalSeconds <= 0 ? 0 : Operations / Elapsed.TotalSeconds;

    public string Summary(string label) =>
        $"{label}: {Operations} ops in {Elapsed.TotalMilliseconds:F0}ms " +
        $"({OperationsPerSecond:F0} ops/s, {Failures} failures) | {Latency}";
}

/// <summary>
/// Runs an async operation from many workers at once, with all workers released
/// from a single starting gate so the contention is real rather than staggered
/// by task scheduling.
/// </summary>
public static class LoadHarness
{
    public static async Task<LoadResult<T>> RunAsync<T>(
        int workers,
        int iterationsPerWorker,
        Func<int, Task<T>> operation,
        CancellationToken ct = default)
    {
        var totalOperations = workers * iterationsPerWorker;
        var results = new T[totalOperations];
        var latencies = new double[totalOperations];
        var failures = new List<Exception>();
        var failureLock = new object();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(workers);

        var workerTasks = new Task[workers];

        for (var w = 0; w < workers; w++)
        {
            var workerIndex = w;
            workerTasks[w] = Task.Run(async () =>
            {
                ready.Signal();
                await gate.Task;

                for (var i = 0; i < iterationsPerWorker; i++)
                {
                    var operationIndex = (workerIndex * iterationsPerWorker) + i;
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        results[operationIndex] = await operation(operationIndex);
                    }
                    catch (Exception ex)
                    {
                        lock (failureLock)
                        {
                            failures.Add(ex);
                        }
                    }
                    finally
                    {
                        latencies[operationIndex] = ElapsedMs(start);
                    }
                }
            }, ct);
        }

        // Give every worker a chance to spin up before releasing the gate, so the
        // first operation of each worker lands in the same window.
        ready.Wait(TimeSpan.FromSeconds(30), ct);

        var runStart = Stopwatch.GetTimestamp();
        gate.SetResult();
        await Task.WhenAll(workerTasks);
        var elapsed = TimeSpan.FromMilliseconds(ElapsedMs(runStart));

        ready.Dispose();

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{failures.Count} of {totalOperations} operations threw during the load run.",
                failures);
        }

        return new LoadResult<T>(results, LatencyStats.FromSamples(latencies), elapsed, failures.Count);
    }

    private static double ElapsedMs(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
}
