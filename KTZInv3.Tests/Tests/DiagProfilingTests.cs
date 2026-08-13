using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// Profiling through the no-op debug seam: loads the real blueprint, runs
    /// actual inventory work (the [P999] ship cargos draining into the [P99]
    /// base cargos) with DEBUGGING=true and a Stopwatch-backed Diag override,
    /// then prints and evaluates the per-label timings.
    ///
    /// This demonstrates the whole point of the seam: the script source never
    /// mentions Stopwatch (illegal in-game), the override lives in THIS assembly
    /// (no whitelist), and the timings come out of the same Main() loop the game
    /// runs every tick.
    /// </summary>
    [TestFixture]
    public class DiagProfilingTests
    {
        static string BlueprintPath
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("KTZINV3_BLUEPRINT");
                if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
                var local = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "TestData", "DockedTest.sbc");
                if (File.Exists(local)) return Path.GetFullPath(local);
                var sent = "/home/user/.hermes/cache/documents/doc_cdc2b2ad2319_bp.sbc";
                if (File.Exists(sent)) return sent;
                throw new FileNotFoundException(
                    "Blueprint not found. Set KTZINV3_BLUEPRINT to the .sbc path, or place it at TestData/DockedTest.sbc");
            }
        }

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ScriptRunner.ResetStatics();
        }

        /// <summary>
        /// Per-label timing accumulator: a running Stopwatch, per-label call
        /// counts and cumulative ticks. Handles nesting (Main wraps Init, etc.)
        /// via an enter-stack keyed by label.
        /// </summary>
        class TimingDiag : IngameScript.Program.DiagBase
        {
            public sealed class LabelStats
            {
                public long Calls;
                public long TotalTicks;
                public long MinTicks = long.MaxValue;
                public long MaxTicks;
                public double TotalMs => TotalTicks * (1000.0 / Stopwatch.Frequency);
                public double AvgMs => TotalTicks * (1000.0 / Stopwatch.Frequency) / Math.Max(1, Calls);
                public double MinMs => MinTicks == long.MaxValue ? 0 : MinTicks * (1000.0 / Stopwatch.Frequency);
                public double MaxMs => MaxTicks * (1000.0 / Stopwatch.Frequency);
            }

            readonly Stopwatch sw = new Stopwatch();
            readonly Dictionary<IngameScript.Program.DbgLabel, LabelStats> stats =
                new Dictionary<IngameScript.Program.DbgLabel, LabelStats>();
            // nesting support: stack of (label, startTick) per enter
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
                s.MaxTicks = Math.Max(s.MaxTicks, elapsed);
                return true;
            }
        }

        [Test]
        public void Profile_InventoryWork_ThroughSeam()
        {
            var world = BlueprintFactory.Load(BlueprintPath);
            var p999Before = TotalAmount(world, "[P999]");
            var p99Before = TotalAmount(world, "[P99]");
            Assert.That((double)p999Before, Is.GreaterThan(0.0), "need [P999] items to move");

            IngameScript.Program.DEBUGGING = true;
            var diag = new TimingDiag();
            IngameScript.Program.diag = diag;

            var runner = ScriptRunner.Create(world.Gts, world.Me);
            var reached = runner.RunUntilUpdateCounter(2);
            Assert.That(reached, Is.True, $"counter 2 not reached in {runner.TicksUsed} ticks");

            var p999After = TotalAmount(world, "[P999]");
            var p99After = TotalAmount(world, "[P99]");

            // ---- print the report ----
            var lines = new List<string>
            {
                $"ticks={runner.TicksUsed} updateCounter={runner.GetGInv()?.updateCounter}",
                $"P999 before={p999Before} after={p999After}",
                $"P99  before={p99Before} after={p99After}",
                "",
                string.Format("{0,-8} {1,8} {2,10} {3,9} {4,9} {5,9}", "label", "calls", "total ms", "avg ms", "min ms", "max ms"),
            };
            foreach (var kvp in diag.Stats.OrderByDescending(kv => kv.Value.TotalMs))
            {
                var s = kvp.Value;
                lines.Add(string.Format("{0,-8} {1,8} {2,10:F2} {3,9:F4} {4,9:F4} {5,9:F4}",
                    kvp.Key, s.Calls, s.TotalMs, s.AvgMs, s.MinMs, s.MaxMs));
            }
            var report = string.Join("\n", lines);
            TestContext.WriteLine("\n" + report);
            Console.WriteLine("\n===== Diag seam profile =====");
            Console.WriteLine(report);
            Console.WriteLine("==============================");

            // ---- evaluate the data ----
            // 1. the work actually happened
            Assert.That((double)p999After, Is.LessThan((double)p999Before), "[P999] must drain");
            Assert.That((double)p99After, Is.GreaterThan((double)p99Before), "[P99] must fill");

            // 2. every label that fired has sane timing data
            Assert.That(diag.Stats.Count, Is.GreaterThanOrEqualTo(8),
                "expected Main/Init/Invu/Stat/Cdbg + Connect/Refinery/Reactor/Conduit to fire during inv work");
            foreach (var kvp in diag.Stats)
            {
                Assert.That(kvp.Value.Calls, Is.GreaterThan(0), $"{kvp.Key} calls");
                Assert.That(kvp.Value.TotalMs, Is.GreaterThan(0), $"{kvp.Key} total");
                Assert.That(kvp.Value.AvgMs, Is.InRange(0, 100), $"{kvp.Key} avg ms sane");
                Assert.That(kvp.Value.MinTicks, Is.LessThanOrEqualTo(kvp.Value.MaxTicks), $"{kvp.Key} min<=max");
            }

            // 3. Main wraps everything else (it's the outermost region)
            if (diag.Stats.TryGetValue(IngameScript.Program.DbgLabel.Main, out var main))
            {
                foreach (var kvp in diag.Stats)
                {
                    if (kvp.Key == IngameScript.Program.DbgLabel.Main) continue;
                    Assert.That(main.TotalMs, Is.GreaterThanOrEqualTo(kvp.Value.TotalMs),
                        $"Main must enclose {kvp.Key} (Main={main.TotalMs:F2}ms {kvp.Key}={kvp.Value.TotalMs:F2}ms)");
                }
            }

            // 4. inv work (Invu) is the dominant consumer - the actual sorting
            Assert.That(diag.Stats.TryGetValue(IngameScript.Program.DbgLabel.Invu, out var invu), Is.True,
                "Invu (inventory sorting) must have fired");
            Assert.That(invu.TotalMs, Is.GreaterThan(0), "Invu timing must be non-zero");
            // the per-call cost of a single block's updateT/updateP cycle must be
            // small (sub-ms per block) or the sorting loop is too heavy
            Assert.That(invu.AvgMs, Is.LessThan(1.0),
                $"per-block inv update should be &lt;1ms, got {invu.AvgMs:F4}ms");

            // 5. balanced: every Enter had an Exit (stack empty at the end)
            Assert.That(diag.StackDepth, Is.Zero, "enter/exit stack must be balanced");
        }

        static MyFixedPoint TotalAmount(BlueprintFactory.World world, string nameToken)
        {
            MyFixedPoint sum = 0;
            for (int i = 0; i < world.Cargos.Count; i++)
                if (world.BlueprintCargos[i].Name.Contains(nameToken))
                    sum += world.Cargos[i].AsFakeInventory().TotalAmount();
            return sum;
        }
    }
}
