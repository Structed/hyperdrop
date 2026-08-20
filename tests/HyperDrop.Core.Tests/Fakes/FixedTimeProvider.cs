namespace HyperDrop.Core.Tests.Fakes;

/// <summary>
/// A clock the test moves by hand, so the update throttle can be exercised without waiting a day.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
