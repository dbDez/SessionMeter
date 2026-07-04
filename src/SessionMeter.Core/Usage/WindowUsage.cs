namespace SessionMeter.Core.Usage;

/// <summary>
/// One rate-limit window's usage as reported by the programmatic OAuth usage endpoint. Claude Code
/// exposes two windows — a rolling 5-hour session window and a 7-day window. The endpoint's raw
/// <c>utilization</c> (0–100 percent OR 0–1 fraction) is normalised to a 0–100 integer here.
/// </summary>
/// <param name="Percent">Utilisation as a whole-number percentage (0–100).</param>
/// <param name="ResetsAt">When this window resets, or null if the endpoint omitted it.</param>
public sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt);
