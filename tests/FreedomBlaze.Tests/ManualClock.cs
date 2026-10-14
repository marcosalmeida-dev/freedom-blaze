namespace FreedomBlaze.Tests;

/// <summary>A clock that stands still until a test moves it.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
