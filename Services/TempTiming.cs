using System.Diagnostics;
using System.Text;

namespace autodraw_plugin.Services;

// TEMP TIMING - measuring where ADSTART's time goes. Delete this file and every
// line marked "TEMP TIMING" once the numbers are in.
public static class TempTiming
{
    private static readonly Stopwatch Clock = new Stopwatch();
    private static readonly StringBuilder Laps = new StringBuilder();
    private static long _last;

    public static void Start()
    {
        Laps.Clear();
        Clock.Restart();
        _last = 0;
    }

    public static void Lap(string what)
    {
        long now = Clock.ElapsedMilliseconds;
        Laps.Append($"\n  {what,-34} {now - _last,6} ms");
        _last = now;
    }

    /// <summary>Drop the time since the last lap - a prompt the user sat on.</summary>
    public static void Skip() => _last = Clock.ElapsedMilliseconds;

    public static string Report() => "\n[timing]" + Laps + $"\n  {"(wall clock incl. prompts)",-34} {Clock.ElapsedMilliseconds,6} ms";
}
