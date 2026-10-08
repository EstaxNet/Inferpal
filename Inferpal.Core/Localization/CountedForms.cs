using System.Globalization;

namespace Inferpal.Localization;

/// <summary>
/// The format provider every <see cref="Strings"/> accessor formats through: it lets a resource write a counted noun as
/// <c>{0:file|files}</c>, and picks the form the interface language uses for that number.
/// </summary>
/// <remarks>
/// <para>A selector carries the forms of ITS language, in CLDR order: two (one|other) in English, French, German,
/// Spanish and Italian, three (one|few|many) in Russian and Polish. Japanese, Korean and Chinese have none: their
/// resources write the noun once. A language may also avoid the question with a label ("Файлов: {0}").</para>
/// <para>⚠ Chosen by the caller (<c>count == 1 ? X1 : X(count)</c>), the singular was right in English and wrong in
/// three languages: French counts zero in the singular, Russian says "21 файл" and "22 файла", Polish "21 plików".
/// The rule belongs to the language, so it lives here, once.</para>
/// <para>Everything that is not a selector formats as <see cref="string.Format(string, object?[])"/> did before: with
/// the current culture, which decides digits and dates (the interface culture only decides the words).</para>
/// </remarks>
internal sealed class CountedForms : IFormatProvider, ICustomFormatter
{
    internal static readonly CountedForms Instance = new();

    private CountedForms() { }

    public object? GetFormat(Type? formatType) =>
        formatType == typeof(ICustomFormatter) ? this : CultureInfo.CurrentCulture.GetFormat(formatType);

    /// <summary>The chosen form for a selector; any other placeholder formatted as usual, with the current culture.</summary>
    public string Format(string? format, object? arg, IFormatProvider? formatProvider)
    {
        if (format is null || format.IndexOf('|') < 0)
            return arg is IFormattable formattable ? formattable.ToString(format, CultureInfo.CurrentCulture) : arg?.ToString() ?? string.Empty;
        var forms = format.Split('|');
        // A number that is not whole ("1.5") takes the last form, which reads as the plural in every language here.
        return TryCount(arg, out var n) ? forms[Index(Strings.UiCulture, n, forms.Length)] : forms[^1];
    }

    /// <summary>Which of <paramref name="forms"/> forms <paramref name="language"/> uses for <paramref name="n"/>.</summary>
    /// <remarks>Decided by how many forms the resource gives, then by the language: an interface language without its
    /// own resources (Dutch, Ukrainian) reads the English ones, two forms, and must count them as English does.</remarks>
    internal static int Index(CultureInfo language, long n, int forms)
    {
        if (forms <= 1) return 0;
        var abs = n < 0 ? -n : n;
        var name = language.TwoLetterISOLanguageName;
        if (forms == 2)
            return name == "fr" ? (abs <= 1 ? 0 : 1) : (abs == 1 ? 0 : 1);

        // one|few|many: Russian counts 21, 31, 101 like 1; Polish has one singular, 1 itself.
        var last = abs % 10;
        var lastTwo = abs % 100;
        var index =
            (name == "pl" ? abs == 1 : last == 1 && lastTwo != 11) ? 0
            : last is >= 2 and <= 4 && lastTwo is < 12 or > 14  ? 1
            : 2;
        return Math.Min(index, forms - 1);
    }

    private static bool TryCount(object? arg, out long n)
    {
        switch (arg)
        {
            case int i: n = i; return true;
            case long l: n = l; return true;
            case short s: n = s; return true;
            case byte b: n = b; return true;
            case uint u: n = u; return true;
            case ushort us: n = us; return true;
            case ulong ul when ul <= long.MaxValue: n = (long)ul; return true;
            default: n = 0; return false;
        }
    }
}
