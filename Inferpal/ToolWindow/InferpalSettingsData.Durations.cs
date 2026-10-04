using System.Runtime.Serialization;
using Inferpal.Services;

namespace Inferpal.ToolWindow;

/// <summary>
/// The four time limits as ONE box in seconds each — what the settings schema declares (<c>UnitSeconds</c>) and the VS
/// Code panel shows. The hour/minute/second boxes behind them stay what Save and the unsaved count read; these only
/// write into them, and follow them when the form is reloaded.
/// </summary>
internal partial class InferpalSettingsData
{
    private string _commandTimeoutText = "", _quickTimeoutText = "", _normalTimeoutText = "", _compactionTimeoutText = "";
    private bool _syncingSeconds;

    [DataMember] public string CommandTimeoutText
    {
        get => _commandTimeoutText;
        set { if (SetProperty(ref _commandTimeoutText, Digits(value))) WriteBack(_commandTimeoutText, (h, m, s) => (TimeoutHoursText, TimeoutMinutesText, TimeoutSecondsText) = (h, m, s)); }
    }
    [DataMember] public string QuickTimeoutText
    {
        get => _quickTimeoutText;
        set { if (SetProperty(ref _quickTimeoutText, Digits(value))) WriteBack(_quickTimeoutText, (h, m, s) => (QuickTimeoutHoursText, QuickTimeoutMinutesText, QuickTimeoutSecondsText) = (h, m, s)); }
    }
    [DataMember] public string NormalTimeoutText
    {
        get => _normalTimeoutText;
        set { if (SetProperty(ref _normalTimeoutText, Digits(value))) WriteBack(_normalTimeoutText, (h, m, s) => (NormalTimeoutHoursText, NormalTimeoutMinutesText, NormalTimeoutSecondsText) = (h, m, s)); }
    }
    [DataMember] public string CompactionTimeoutText
    {
        get => _compactionTimeoutText;
        set { if (SetProperty(ref _compactionTimeoutText, Digits(value))) WriteBack(_compactionTimeoutText, (h, m, s) => (CompactionTimeoutHoursText, CompactionTimeoutMinutesText, CompactionTimeoutSecondsText) = (h, m, s)); }
    }

    /// <summary>The names of the hour/minute/second boxes: a change there (Cancel, a reload) redraws the seconds.</summary>
    private static readonly HashSet<string> DurationParts = new(StringComparer.Ordinal)
    {
        nameof(TimeoutHoursText), nameof(TimeoutMinutesText), nameof(TimeoutSecondsText),
        nameof(QuickTimeoutHoursText), nameof(QuickTimeoutMinutesText), nameof(QuickTimeoutSecondsText),
        nameof(NormalTimeoutHoursText), nameof(NormalTimeoutMinutesText), nameof(NormalTimeoutSecondsText),
        nameof(CompactionTimeoutHoursText), nameof(CompactionTimeoutMinutesText), nameof(CompactionTimeoutSecondsText),
    };

    /// <summary>Digits only, six at most: 999,999 s is already past the 99 hours the boxes behind can hold.</summary>
    private static string Digits(string? value) => new([.. (value ?? string.Empty).Where(char.IsDigit).Take(6)]);

    /// <summary>A seconds box into its three boxes. Emptied, all three empty — the "cleared, so the default" of Save.</summary>
    private void WriteBack(string seconds, Action<string, string, string> set)
    {
        if (_syncingSeconds) return;
        _syncingSeconds = true;
        try
        {
            if (seconds.Length == 0) set(string.Empty, string.Empty, string.Empty);
            else
            {
                var (h, m, s) = DurationFields.Split(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture));
                set(h, m, s);
            }
        }
        finally { _syncingSeconds = false; }
    }

    /// <summary>The seconds boxes from the three boxes behind them — after a load, a Cancel, a save.</summary>
    private void SyncSecondsBoxes()
    {
        if (_syncingSeconds) return;
        _syncingSeconds = true;
        try
        {
            SetProperty(ref _commandTimeoutText,    Seconds(TimeoutHoursText, TimeoutMinutesText, TimeoutSecondsText), nameof(CommandTimeoutText));
            SetProperty(ref _quickTimeoutText,      Seconds(QuickTimeoutHoursText, QuickTimeoutMinutesText, QuickTimeoutSecondsText), nameof(QuickTimeoutText));
            SetProperty(ref _normalTimeoutText,     Seconds(NormalTimeoutHoursText, NormalTimeoutMinutesText, NormalTimeoutSecondsText), nameof(NormalTimeoutText));
            SetProperty(ref _compactionTimeoutText, Seconds(CompactionTimeoutHoursText, CompactionTimeoutMinutesText, CompactionTimeoutSecondsText), nameof(CompactionTimeoutText));
        }
        finally { _syncingSeconds = false; }

        static string Seconds(string h, string m, string s) =>
            string.IsNullOrWhiteSpace(h) && string.IsNullOrWhiteSpace(m) && string.IsNullOrWhiteSpace(s)
                ? string.Empty
                : DurationFields.Combine(h, m, s).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
