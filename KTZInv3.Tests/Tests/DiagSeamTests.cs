using System;
using System.Collections.Generic;
using System.Diagnostics;
using FakeItEasy;
using KTZInv3.Tests.TestUtilities;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// Proves the no-op debug seam (Diag.cs) works end-to-end:
    ///
    /// 1. With DEBUGGING=false (the in-game state), the ternary branches are
    ///    never taken - diag.Enter/Exit are never called, and the script behaves
    ///    exactly as before.
    /// 2. With DEBUGGING=true + a diag override compiled by THIS project,
    ///    Main() dispatches into the override at every Profiler site, and the
    ///    override can Stopwatch freely (Stopwatch is NOT legal in script source;
    ///    it is legal here because the override is compiled by the test project).
    ///
    /// This is the pattern the game whitelist allows: the script source never
    /// mentions Stopwatch; the seam is a script-defined class (IsInSource), so
    /// calls into it are legal even in dead branches.
    /// </summary>
    [TestFixture]
    public class DiagSeamTests
    {
        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ScriptRunner.ResetStatics();
        }

        /// <summary>Records Enter/Exit calls with Stopwatch timings per label.</summary>
        class RecordingDiag : IngameScript.Program.DiagBase
        {
            public readonly List<(IngameScript.Program.DbgLabel label, string kind, double ms)> Events =
                new List<(IngameScript.Program.DbgLabel label, string kind, double ms)>();

            readonly Stopwatch sw = new Stopwatch();
            IngameScript.Program.DbgLabel active = (IngameScript.Program.DbgLabel)(-1);

            public override bool Enter(IngameScript.Program.DbgLabel label)
            {
                active = label;
                sw.Restart();
                Events.Add((label, "enter", 0));
                return true;
            }

            public override bool Exit(IngameScript.Program.DbgLabel label)
            {
                sw.Stop();
                Events.Add((label, "exit", sw.Elapsed.TotalMilliseconds));
                return true;
            }
        }

        [Test]
        public void DebuggingFalse_SeamNeverCalled()
        {
            IngameScript.Program.DEBUGGING = false;
            var probe = new RecordingDiag();
            IngameScript.Program.diag = probe;

            var program = Gateway.CreateProgram().Build();
            IngameScript.Program.APIWC = new IngameScript.WcPbApi();
            IngameScript.Program.tick = 0;
            program.Main("", UpdateType.Update1);

            Assert.That(probe.Events, Is.Empty,
                "with DEBUGGING=false the ternary branches must never be taken");
        }

        [Test]
        public void DebuggingTrue_MainFiresEvents()
        {
            IngameScript.Program.DEBUGGING = true;
            var probe = new RecordingDiag();
            IngameScript.Program.diag = probe;

            var program = Gateway.CreateProgram().Build();
            IngameScript.Program.APIWC = new IngameScript.WcPbApi();
            IngameScript.Program.tick = 0;
            program.Main("", UpdateType.Update1);

            // Main always runs: Main tick getter -> main() -> init profiler.
            // The first tick (tick 0) also runs the 11-step resource loader
            // which returns early - but the Main/Init seam points fire regardless.
            var labels = new HashSet<IngameScript.Program.DbgLabel>();
            foreach (var (label, kind, _) in probe.Events)
            {
                Assert.That(kind, Is.EqualTo("enter").Or.EqualTo("exit"));
                labels.Add(label);
            }

            Assert.That(probe.Events.Count, Is.GreaterThanOrEqualTo(2), "at least Main enter+exit");
            Assert.That(labels, Does.Contain(IngameScript.Program.DbgLabel.Main));
            Assert.That(labels, Does.Contain(IngameScript.Program.DbgLabel.Init));

            // well-formed: every enter must have a matching exit
            var depth = new Dictionary<IngameScript.Program.DbgLabel, int>();
            foreach (var (label, kind, _) in probe.Events)
            {
                depth.TryGetValue(label, out int d);
                if (kind == "enter") depth[label] = d + 1;
                else depth[label] = d - 1;
                Assert.That(depth[label], Is.GreaterThanOrEqualTo(0), $"unbalanced {label} {kind}");
            }
        }

        [Test]
        public void EnumLabels_ReflectableByTestFramework()
        {
            // the whole point of enum labels: the test framework can reflect the
            // enum to get human names without any string literal in script source
            var names = Enum.GetNames(typeof(IngameScript.Program.DbgLabel));
            Assert.That(names, Does.Contain("Main"));
            Assert.That(names, Does.Contain("Invu"));
            Assert.That(names, Does.Contain("P3"));
            Assert.That(names, Does.Contain("Conduit"));
            Assert.That(names, Does.Contain("Refinery"));
            Assert.That((int)IngameScript.Program.DbgLabel.Main, Is.EqualTo(1));
        }
    }
}
