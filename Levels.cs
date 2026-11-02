namespace TaskbarMonitor;

/// <summary>Traffic-light colors by level, used for the rings and tray icon fills.</summary>
internal static class Levels
{
    public static readonly Color Green = Color.FromArgb(46, 184, 92);
    public static readonly Color Yellow = Color.FromArgb(240, 190, 20);
    public static readonly Color Orange = Color.FromArgb(243, 128, 33);
    public static readonly Color Red = Color.FromArgb(232, 56, 56);

    // Usage %:        green < 60 <= yellow < 80 <= orange < 95 <= red
    // GPU temp (°C):  green < 65 <= yellow < 80 <= orange < 88 <= red  (most GPUs start throttling in the 80s)
    public static Color For(Metrics metric, float value)
    {
        var (yellow, orange, red) = metric == Metrics.Temp ? (65f, 80f, 88f) : (60f, 80f, 95f);
        return value >= red ? Red : value >= orange ? Orange : value >= yellow ? Yellow : Green;
    }
}
