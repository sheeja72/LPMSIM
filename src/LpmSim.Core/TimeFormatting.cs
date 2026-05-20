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

    /// <summary>
    /// 1.14.75 — Current wall-clock time in the GCC zone (UTC+4).
    /// Returns a <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/>
    /// (because the value is in GST, not UTC and not the server's local
    /// time). Used by the nightly Build SKU Max scheduler to compute
    /// "what year / month is it in Dubai right now?".
    /// </summary>
    public static DateTime NowGst()
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Gcc);

    /// <summary>
    /// 1.14.75 — Returns the next UTC instant when the GCC local clock reads
    /// <paramref name="targetGstTime"/>. If the GST moment has already passed
    /// today (in GST), returns tomorrow's instance. Used by the scheduler to
    /// compute "how long until the next 04:00 GST?".
    /// </summary>
    public static DateTime NextGstUtc(TimeOnly targetGstTime)
    {
        var nowGst         = NowGst();
        var todayTargetGst = new DateTime(
            nowGst.Year, nowGst.Month, nowGst.Day,
            targetGstTime.Hour, targetGstTime.Minute, 0,
            DateTimeKind.Unspecified);

        if (todayTargetGst <= nowGst)
            todayTargetGst = todayTargetGst.AddDays(1);

        // Convert the GST wall-clock instant to UTC. Kind=Unspecified +
        // explicit timezone is the safe pattern (Kind=Local would assume
        // the server's local timezone, which on Azure App Service is UTC).
        return TimeZoneInfo.ConvertTimeToUtc(todayTargetGst, Gcc);
    }
}
