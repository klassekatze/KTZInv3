using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// Stopwatch-backed override of the script's no-op Diag seam. Collected
    /// per-label call counts and tick timings (with nesting support via an
    /// enter-stack), printable as a sorted report.
    ///
    /// Lives in the TEST assembly so it can use Stopwatch (illegal in-game);
    /// the seam's virtual dispatch means the test-side override carries zero
    /// injected instructions. Set IngameScript.Program.DEBUGGING = true and
    /// IngameScript.Program.diag = this before running Main.
    /// </summary>
    public sealed class TimingDiag : IngameScript.Program.DiagBase
    {
        public sealed class LabelStats
        {
            public long Calls;
            public long TotalTicks;
            public long MinTicks = long.MaxValue;
            public long MaxTicks;
            public long MaxAtCall; // 1-based ordinal of the call that hit MaxTicks
            public double TotalMs => TotalTicks * (1000.0 / Stopwatch.Frequency);
            public double AvgMs => TotalTicks * (1000.0 / Stopwatch.Frequency) / Math.Max(1, Calls);
            public double MinMs => MinTicks == long.MaxValue ? 0 : MinTicks * (1000.0 / Stopwatch.Frequency);
            public double MaxMs => MaxTicks * (1000.0 / Stopwatch.Frequency);
        }

        readonly Stopwatch sw = new Stopwatch();
        readonly Dictionary<IngameScript.Program.DbgLabel, LabelStats> stats =
            new Dictionary<IngameScript.Program.DbgLabel, LabelStats>();
        readonly Stack<(IngameScript.Program.DbgLabel label, long start)> stack =
            new Stack<(IngameScript.Program.DbgLabel, long)>();

        public IReadOnlyDictionary<IngameScript.Program.DbgLabel, LabelStats> Stats => stats;
        public int StackDepth => stack.Count;

        public override bool Enter(IngameScript.Program.DbgLabel label)
        {
            if (!sw.IsRunning) sw.Start();
            stack.Push((label, sw.ElapsedTicks));
            return true;
        }

        public override bool Exit(IngameScript.Program.DbgLabel label)
        {
            long end = sw.ElapsedTicks;
            if (stack.Count == 0) return true;
            var (l, start) = stack.Pop();
            long elapsed = end - start;
            if (!stats.TryGetValue(l, out var s)) { s = new LabelStats(); stats[l] = s; }
            s.Calls++;
            s.TotalTicks += elapsed;
            s.MinTicks = Math.Min(s.MinTicks, elapsed);
            if (elapsed > s.MaxTicks) { s.MaxTicks = elapsed; s.MaxAtCall = s.Calls; }
            return true;
        }

        /// <summary>Prints a sorted per-label report and returns it as a string.</summary>
        public string Report(string header = "")
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(header)) lines.Add(header);
            lines.Add(string.Format("{0,-14} {1,8} {2,10} {3,9} {4,9} {5,9} {6,9}", "label", "calls", "total ms", "avg ms", "min ms", "max ms", "max@call"));
            foreach (var kvp in stats.OrderByDescending(kv => kv.Value.TotalMs))
            {
                var s = kvp.Value;
                lines.Add(string.Format("{0,-14} {1,8} {2,10:F2} {3,9:F4} {4,9:F4} {5,9:F4} {6,9}",
                    kvp.Key, s.Calls, s.TotalMs, s.AvgMs, s.MinMs, s.MaxMs, s.MaxAtCall));
            }
            return string.Join("\n", lines);
        }
    }
}
