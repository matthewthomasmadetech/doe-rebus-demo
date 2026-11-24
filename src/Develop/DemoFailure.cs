namespace RebusDemo.Develop;

/// <summary>
/// A process-wide switch for demonstrating a transient handler failure when enabled.
/// </summary>
public sealed class DemoFailure(bool enabled)
{
    private int _remainingFailures = enabled ? 1 : 0;

    /// <summary>
    /// Atomically clears the switch and returns true only to the first caller when enabled.
    /// </summary>
    public bool ConsumeFailure() => Interlocked.Exchange(ref _remainingFailures, 0) == 1;
}
