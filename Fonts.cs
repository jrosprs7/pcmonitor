namespace TaskbarMonitor;

/// <summary>The compact (condensed) font used for all text drawn on the rings and tray icons.</summary>
internal static class Fonts
{
    private static (FontFamily Family, FontStyle Style)? _compact;

    // First installed font wins. Bahnschrift ships with Windows 10 (1709+) and 11; its condensed
    // styles are exposed as separate families, with the weight already built in (so Style = Regular).
    private static readonly (string Name, FontStyle Style)[] Candidates =
    {
        ("Bahnschrift SemiBold Condensed", FontStyle.Regular),
        ("Bahnschrift Condensed", FontStyle.Bold),
        ("Arial Narrow", FontStyle.Bold),
        ("Segoe UI", FontStyle.Bold),
    };

    public static FontFamily Family => (_compact ??= Pick()).Family;
    public static int Style => (int)(_compact ??= Pick()).Style;

    private static (FontFamily, FontStyle) Pick()
    {
        foreach (var (name, style) in Candidates)
        {
            try
            {
                var f = new FontFamily(name); // throws if the font isn't installed
                if (f.IsStyleAvailable(style)) return (f, style);
                f.Dispose();
            }
            catch (ArgumentException) { }
        }
        return (FontFamily.GenericSansSerif, FontStyle.Bold);
    }
}
