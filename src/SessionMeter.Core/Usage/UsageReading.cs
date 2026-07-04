namespace SessionMeter.Core.Usage;

/// <summary>
/// A parsed <c>/usage</c> reading. A null <see cref="Percent"/> means the parse failed and the caller
/// should skip the tick rather than act on a bad number.
/// </summary>
/// <param name="Percent">Session/5-hour usage percentage, or null if unmatched.</param>
/// <param name="ResetAt">When the window resets, or null if unmatched.</param>
/// <param name="Raw">The raw captured text the reading came from.</param>
public sealed record UsageReading(int? Percent, DateTimeOffset? ResetAt, string Raw);
