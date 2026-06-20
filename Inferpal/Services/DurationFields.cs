using System.Linq;

namespace Inferpal.Services;

/// <summary>
/// Pure conversions for the settings h/min/s duration boxes (command / quick / normal / deep /
/// compaction timeouts), extracted from <c>InferpalSettingsData</c> so the math is unit-testable
/// without VS. Splits a total-seconds value into display sub-fields, sanitises a sub-field as the
/// user types, and recombines the sub-fields back into total seconds.
/// </summary>
internal static class DurationFields
{
    /// <summary>
    /// Sanitises a duration sub-field (h/min/s box) live as the user types: keeps digits only and
    /// clamps to <c>[0, max]</c>. Empty is preserved so the field can be cleared mid-edit (it resolves
    /// to 0 at save time via <see cref="Combine"/>).
    /// </summary>
    public static string Clamp(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, out var n)) return string.Empty;
        return Math.Min(n, max).ToString();
    }

    /// <summary>Splits a total-seconds duration into the (hours, min, sec) display strings — hours
    /// plain, minutes/seconds zero-padded. Negative input is treated as 0.</summary>
    public static (string h, string m, string s) Split(int totalSeconds)
    {
        var t = TimeSpan.FromSeconds(totalSeconds < 0 ? 0 : totalSeconds);
        return (((int)t.TotalHours).ToString(), t.Minutes.ToString("D2"), t.Seconds.ToString("D2"));
    }

    /// <summary>Recombines the (already-clamped) h/min/s sub-fields back into total seconds;
    /// non-numeric or empty sub-fields count as 0.</summary>
    public static int Combine(string h, string m, string s) =>
          (int.TryParse(h, out var hv) ? hv : 0) * 3600
        + (int.TryParse(m, out var mv) ? mv : 0) * 60
        + (int.TryParse(s, out var sv) ? sv : 0);
}
