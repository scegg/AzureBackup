namespace AzureBackup.Core.Storage;

/// <summary>
/// Backoff retry for transient storage failures. Default schedule (from requirements):
/// 5s, 30s, 90s, 300s, then every 300s, with a 2-hour total cap; then it gives up.
/// The delay function is injectable so tests run instantly.
/// </summary>
public sealed class RetryPolicy
{
    private readonly TimeSpan[] _schedule;
    private readonly TimeSpan _steady;
    private readonly TimeSpan _maxTotal;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RetryPolicy(
        IEnumerable<TimeSpan> initialDelays,
        TimeSpan steadyDelay,
        TimeSpan maxTotal,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _schedule = [.. initialDelays];
        _steady = steadyDelay;
        _maxTotal = maxTotal;
        _delay = delay ?? Task.Delay;
    }

    public static RetryPolicy Default(Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
        initialDelays:
        [
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(300),
        ],
        steadyDelay: TimeSpan.FromSeconds(300),
        maxTotal: TimeSpan.FromHours(2),
        delay: delay);

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, bool> isTransient,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isTransient);

        TimeSpan elapsed = TimeSpan.Zero;
        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (isTransient(ex))
            {
                TimeSpan wait = attempt < _schedule.Length ? _schedule[attempt] : _steady;
                attempt++;
                if (elapsed + wait > _maxTotal)
                    throw; // exceeded total budget — give up
                await _delay(wait, ct).ConfigureAwait(false);
                elapsed += wait;
            }
        }
    }

    public Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        Func<Exception, bool> isTransient,
        CancellationToken ct = default)
        => ExecuteAsync<object?>(async c => { await operation(c).ConfigureAwait(false); return null; }, isTransient, ct);
}
