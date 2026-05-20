namespace LpmSim.Core;

/// <summary>
/// 1.14.70 — Centralised helpers for showing UTC-stored timestamps in GCC
/// time (Arabian Standard Time, UTC+4, no DST) — the timezone every LpmSim
/// user operates in.
///
/// <para>
/// Azure App Service runs in UTC by default (unless <c>WEBSITE_TIME_ZONE</c>
/// has been overridden), so <c>DateTime.Now</c> and SQL Server's <c>datetime</c>
/// columns end up holding UTC wall-clock values. Without this conversion,
/// every "Generated" / "Approved" / "Created" timestamp on the UI shows UTC,
/// which is 4 hours behind what GCC-based planners actually expect to see.
/// </para>
///
/// <para>
/// We try the IANA ID <c>"Asia/Dubai"</c> first (works on Linux and recent
/// .NET on Windows), fall back to the Windows ID <c>"Arabian Standard Time"</c>,
/// and as a last resort construct a fixed-offset zone in code so the UI never
/// crashes due to a missing tz database entry.
/// </para>
/// </summary>
public static class TimeFormatting
{
    private static readonly TimeZoneInfo Gcc = ResolveGccZone();

    private static TimeZoneInfo ResolveGccZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        try { return TimeZoneInfo.FindSystemTimeZoneById("Arabian Standard Time"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        // Final fallback: hand-built fixed-offset zone. Won't appear in tz
        // listings but does the +4h conversion correctly.
        return TimeZoneInfo.CreateCustomTimeZone("GCC", TimeSpan.FromHours(4), "GCC (UTC+4)", "GST");
    }

    /// <summary>
    /// Convert a UTC-stored <see cref="DateTime"/> to GCC time and return a
    /// formatted string with a trailing <c>" GST"</c> suffix. Returns an
    /// empty string when the input is <c>null</c>.
    /// </summary>
    /// <param name="utc">The UTC-stored value (typically read from SQL Server
    /// or produced by <see cref="DateTime.Now"/> on a UTC-configured
    /// Azure App Service).</param>
    /// <param name="format">Standard .NET date/time format string. Defaults
    /// to <c>"yyyy-MM-dd HH:mm"</c>.</param>
    public static string ToGccString(DateTime? utc, string format = "yyyy-MM-dd HH:mm")
    {
        if (!utc.HasValue) return string.Empty;
        // Force the Kind so ConvertTimeFromUtc treats the value as UTC.
        // SQL Server returns Kind=Unspecified, which ConvertTimeFromUtc
        // would otherwise reject.
        var asUtc = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        var gst   = TimeZoneInfo.ConvertTimeFromUtc(asUtc, Gcc);
        return gst.ToString(format) + " GST";
    }

    /// <summary>
    /// Non-nullable overload — convenient at call sites where the caller
    /// already null-checked the value.
    /// </summary>
    public static string ToGccString(DateTime utc, string format = "yyyy-MM-dd HH:mm")
        => ToGccString((DateTime?)utc, format);
}
