using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

internal sealed class FrameSample
{
    internal readonly List<double> Intervals = new(700);
    internal readonly List<double> Work = new(700);

    internal string Summarize()
    {
        if (Intervals.Count == 0) return "no animation callbacks";
        int over25 = Intervals.Count(interval => interval > 25);
        Intervals.Sort();
        Work.Sort();
        return $"intervals={Intervals.Count}, p50={Percentile(Intervals, .5):F1} ms, "
            + $"p95={Percentile(Intervals, .95):F1} ms, max={Intervals[^1]:F1} ms, >25 ms={over25}, "
            + $"simulation p95={Percentile(Work, .95):F2} ms";
    }

    private static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Ceiling(sorted.Count * p) - 1];
}
