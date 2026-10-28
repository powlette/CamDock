using System.Globalization;

namespace CamDock.Core;

/// <summary>Decides whether alerts are currently held back by a snooze or by scheduled quiet hours.</summary>
internal static class AlertGate
{
    public static bool TryParseTime(string? s, out TimeSpan t) =>
        TimeSpan.TryParseExact(s?.Trim() ?? "", new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out t);

    public static bool IsSnoozed(AppConfig cfg, DateTime utcNow) => cfg.SnoozeUntilUtc is { } u && u > utcNow;

    /// <summary>True while inside a configured quiet-hours window (handles windows that cross midnight).</summary>
    public static bool IsQuietHours(AppConfig cfg, DateTime localNow)
    {
        if (!cfg.QuietHoursEnabled || cfg.QuietDays.Count == 0) return false;
        if (!TryParseTime(cfg.QuietStart, out var start) || !TryParseTime(cfg.QuietEnd, out var end) || start == end) return false;
        var t = localNow.TimeOfDay;
        var today = localNow.DayOfWeek;
        var yesterday = localNow.AddDays(-1).DayOfWeek;
        if (start < end) return cfg.QuietDays.Contains(today) && t >= start && t < end;
        // overnight, e.g. 22:00 → 07:00: the window belongs to the day it starts on
        return (t >= start && cfg.QuietDays.Contains(today)) || (t < end && cfg.QuietDays.Contains(yesterday));
    }

    /// <summary>Null when alerts are on; otherwise a short reason for the tray ("Snoozed until 3:45 PM").</summary>
    public static string? HoldReason(AppConfig cfg)
    {
        var now = DateTime.UtcNow;
        if (IsSnoozed(cfg, now)) return $"Snoozed until {cfg.SnoozeUntilUtc!.Value.ToLocalTime():t}";
        if (IsQuietHours(cfg, DateTime.Now))
        {
            TryParseTime(cfg.QuietEnd, out var end);
            return $"Quiet hours until {DateTime.Today.Add(end):t}";
        }
        return null;
    }

    /// <summary>Should an alert of this kind be shown right now?</summary>
    public static bool Allows(AppConfig cfg, bool isDoorbell)
    {
        if (HoldReason(cfg) == null) return true;
        return isDoorbell && cfg.DoorbellBreaksThrough;
    }

    /// <summary>Next occurrence of the quiet-hours end time, or 7:00 tomorrow by default.</summary>
    public static DateTime NextMorningUtc(AppConfig cfg)
    {
        var end = TryParseTime(cfg.QuietEnd, out var e) ? e : TimeSpan.FromHours(7);
        var local = DateTime.Today.Add(end);
        if (local <= DateTime.Now) local = local.AddDays(1);
        return local.ToUniversalTime();
    }
}
