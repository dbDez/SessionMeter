using System.Globalization;

namespace SessionMeter.Core.Util;

/// <summary>
/// Machine-local rendering for every operator-facing instant (productization rule: never hardcode a
/// timezone). All internal times stay UTC-anchored (<see cref="DateTimeOffset"/>); the moment one is SHOWN
/// to the operator — CLI line, log line — it goes through here, converting via
/// <see cref="TimeZoneInfo.Local"/> PER-INSTANT so the rendering is correct on either side of a DST boundary
/// on any machine this is installed on.
/// </summary>
public static class LocalClock
{
    /// <summary>
    /// Formats an instant in the machine's local zone as <c>yyyy-MM-dd HH:mm local</c>. The conversion is
    /// per-instant (<see cref="TimeZoneInfo.ConvertTime(DateTimeOffset, TimeZoneInfo)"/>), not a cached
    /// offset, so an instant that falls on the other side of a DST transition renders at its own offset.
    /// </summary>
    /// <param name="t">The instant to render (any offset; typically UTC from the usage endpoint).</param>
    public static string FormatLocal(DateTimeOffset t)
        => TimeZoneInfo.ConvertTime(t, TimeZoneInfo.Local)
            .ToString("yyyy-MM-dd HH:mm 'local'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Rounds an instant UP to the next whole minute (the reset-time display rule): an API reset of
    /// 18:09:59 must read as 18:10 (the wall opens at the displayed minute), never a truncated 18:09.
    /// An instant already on a whole minute is returned unchanged. Pure and offset-preserving (every real
    /// zone offset is whole minutes, so the minute boundary is the same in local and UTC).
    /// </summary>
    /// <param name="t">The instant to round up.</param>
    public static DateTimeOffset CeilToMinute(DateTimeOffset t)
    {
        long rem = t.Ticks % TimeSpan.TicksPerMinute;
        return rem == 0 ? t : t.AddTicks(TimeSpan.TicksPerMinute - rem);
    }

    /// <summary>
    /// Formats a RESET instant for display: ceiled to the minute (<see cref="CeilToMinute"/>) then rendered
    /// via <see cref="FormatLocal"/>. The ONE formatting policy every reset-time surface shares so 18:09:59
    /// never truncates to 18:09 anywhere. Non-reset instants (e.g. "as of" stamps) keep plain
    /// <see cref="FormatLocal"/>.
    /// </summary>
    /// <param name="t">The reset instant to render (any offset; typically UTC from the usage endpoint).</param>
    public static string FormatResetLocal(DateTimeOffset t) => FormatLocal(CeilToMinute(t));

    /// <summary>
    /// Describes the machine's zone for prose surfaces — e.g. <c>South Africa Standard Time (UTC+02:00)</c> —
    /// built from <see cref="TimeZoneInfo.Local"/>'s name plus the CURRENT offset.
    /// <see cref="TimeZoneInfo.StandardName"/> is preferred over <c>DisplayName</c> because it matches the
    /// prose shape ("… Standard Time") and stays stable under <c>InvariantGlobalization</c>; a zone with no
    /// standard name falls back to its IANA/Windows id.
    /// </summary>
    public static string DescribeZone()
    {
        TimeZoneInfo tz = TimeZoneInfo.Local;
        TimeSpan offset = tz.GetUtcOffset(DateTimeOffset.UtcNow);
        string name = string.IsNullOrWhiteSpace(tz.StandardName) ? tz.Id : tz.StandardName;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{name} (UTC{sign}{offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)})";
    }
}
