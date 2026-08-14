using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using FakeItEasy;
using NUnit.Framework;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;
using VRage.ObjectBuilders;
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
        static readonly MyItemType IronOre = new MyItemType("MyObjectBuilder_Ore", "Iron");
        static readonly MyItemType CopperOre = new MyItemType("MyObjectBuilder_Ore", "Copper");
        static readonly MyItemType LeadOre = new MyItemType("MyObjectBuilder_Ore", "Lead");
        static readonly MyItemType NickelOre = new MyItemType("MyObjectBuilder_Ore", "Nickel");
        static readonly MyItemType SiliconOre = new MyItemType("MyObjectBuilder_Ore", "Silicon");
        static readonly MyItemType StoneOre = new MyItemType("MyObjectBuilder_Ore", "Stone");
        static readonly MyItemType IronIngot = new MyItemType("MyObjectBuilder_Ingot", "Iron");
        static readonly MyItemType CopperIngot = new MyItemType("MyObjectBuilder_Ingot", "Copper");
        static readonly MyItemType LeadIngot = new MyItemType("MyObjectBuilder_Ingot", "Lead");
        static readonly MyItemType NickelIngot = new MyItemType("MyObjectBuilder_Ingot", "Nickel");
        static readonly MyItemType SiliconIngot = new MyItemType("MyObjectBuilder_Ingot", "Silicon");
        static readonly MyItemType PowerCell = new MyItemType("MyObjectBuilder_Component", "PowerCell");
        static readonly MyDefinitionId PowerCellBp = new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "sdx_itemsBlueprintT0PowerCell");
        static readonly MyDefinitionId LargeRefineryDef = new MyDefinitionId(typeof(MyObjectBuilder_Refinery), "LargeRefinery");
        static readonly BindingFlags NF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        static void SeedComposition(MyItemType item, Dictionary<MyItemType, MyFixedPoint> comp)
        {
            var known = (Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>)typeof(IngameScript.Program)
                .GetNestedType("AsmLearn", BindingFlags.NonPublic).GetField("known", NF).GetValue(null);
            known[item] = comp;
        }

        static void SeedRefineryRecipe(params (MyItemType ore, MyItemType ingot, double ratio)[] recipes)
        {
            var learned = (Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>)typeof(IngameScript.Program)
                .GetNestedType("RefLearn", BindingFlags.NonPublic).GetField("learned", NF).GetValue(null);
            var byOre = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();
            foreach (var (ore, ingot, ratio) in recipes)
            {
                Dictionary<MyItemType, MyFixedPoint> outs;
                if (!byOre.TryGetValue(ore, out outs)) { outs = new Dictionary<MyItemType, MyFixedPoint>(); byOre[ore] = outs; }
                outs[ingot] = (MyFixedPoint)ratio;
            }
            learned[LargeRefineryDef] = byOre;
        }
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
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Copper", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Lead", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Nickel", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Silicon", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Copper", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Lead", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Nickel", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Silicon", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Component", "PowerCell", 0.0001f, 0.5f, (MyFixedPoint)1000);
            ScriptRunner.ResetStatics();
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
                "expected Main/Init/InvBlocks/StatusGen/PassStart + ConnectEvents/Refinery/Reactor/Conduit to fire during inv work");
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

            // 4. inv work (InvBlocks) is the dominant consumer - the actual sorting
            Assert.That(diag.Stats.TryGetValue(IngameScript.Program.DbgLabel.InvBlocks, out var invu), Is.True,
                "InvBlocks (inventory sorting) must have fired");
            Assert.That(invu.TotalMs, Is.GreaterThan(0), "InvBlocks timing must be non-zero");
            // the per-call cost of a single block's updateT/updateP cycle must be
            // small (sub-ms per block) or the sorting loop is too heavy
            Assert.That(invu.AvgMs, Is.LessThan(1.0),
                $"per-block inv update should be &lt;1ms, got {invu.AvgMs:F4}ms");

            // 5. balanced: every Enter had an Exit (stack empty at the end)
            Assert.That(diag.StackDepth, Is.Zero, "enter/exit stack must be balanced");
        }

        [Test]
        public void Profile_QueuePriorityAndDiscovery_ThroughSeam()
        {
            // Realistic steady-state scenario: 1 refinery, 1 assembler with
            // a 999 PowerCell queue, cargo stocked with ores + the live
            // ingot amounts. All recipes known (no discovery starts). Runs
            // Main() for 130+ ticks so the once-per-second scan seams
            // (RefScan/AsmScan at tick%60) fire, and prints the per-label
            // report to find bottlenecks.
            var grid = CargoFactory.CreateGrid();
            var cargo = CargoFactory.CreateCargo("1 Cargo [Ore].P99", (MyFixedPoint)400.0, grid,
                (IronOre, (MyFixedPoint)10000), (CopperOre, (MyFixedPoint)10000),
                (LeadOre, (MyFixedPoint)10000), (NickelOre, (MyFixedPoint)10000),
                (SiliconOre, (MyFixedPoint)10000), (StoneOre, (MyFixedPoint)10000),
                (IronIngot, (MyFixedPoint)1393), (SiliconIngot, (MyFixedPoint)45),
                (NickelIngot, (MyFixedPoint)26), (LeadIngot, (MyFixedPoint)0.1m),
                (CopperIngot, (MyFixedPoint)152));

            var (refinery, _) = MakeProfilingRefinery(grid);
            var (asm, state) = MakeProfilingAssembler(grid);
            state.Mode = MyAssemblerMode.Assembly;
            state.Queue.Add(new MyProductionItem(0, PowerCellBp, (MyFixedPoint)999));

            var gts = new FakeGts();
            gts.Blocks.Add(cargo.Block);
            gts.Blocks.Add(refinery);
            gts.Blocks.Add(asm);

            var me = MeFactory.CreateMe(grid);
            var runner = ScriptRunner.Create(gts, me);

            // seed the full registry: blueprint mapping, composition, recipes
            var blueprints = (System.Collections.IDictionary)typeof(IngameScript.Program)
                .GetNestedType("Autocraft", BindingFlags.NonPublic).GetField("blueprints", NF).GetValue(null);
            blueprints[(MyDefinitionId)PowerCell] = PowerCellBp;
            SeedComposition(PowerCell, new Dictionary<MyItemType, MyFixedPoint> {
                { IronIngot, (MyFixedPoint)7 }, { SiliconIngot, (MyFixedPoint)0.7m },
                { NickelIngot, (MyFixedPoint)1 }, { LeadIngot, (MyFixedPoint)0.7m },
                { CopperIngot, (MyFixedPoint)3 } });
            SeedRefineryRecipe(
                (IronOre, IronIngot, 0.7),
                (SiliconOre, SiliconIngot, 0.7),
                (NickelOre, NickelIngot, 0.4),
                (LeadOre, LeadIngot, 0.16),
                (CopperOre, CopperIngot, 0.24),
                (StoneOre, IronIngot, 0.03), (StoneOre, NickelIngot, 0.002), (StoneOre, SiliconIngot, 0.004));

            IngameScript.Program.DEBUGGING = true;
            var diag = new TimingDiag();
            IngameScript.Program.diag = diag;

            runner.Build();
            int ticks = 0;
            while (ticks < 400 && IngameScript.Program.tick < 130)
            {
                runner.Program.Main("", UpdateType.Update1);
                ticks++;
            }

            var report = diag.Report($"ticks={IngameScript.Program.tick} mainCalls={ticks}");
            var inv = runner.GetGInv();
            TestContext.WriteLine("\n" + report);
            Console.WriteLine("\n===== QueuePriority/discovery profile =====");
            Console.WriteLine(report);
            Console.WriteLine($"updateCounter={inv?.updateCounter}");
            Console.WriteLine("===========================================");

            var s = diag.Stats;
            // the new seams all fired
            Assert.That(s.TryGetValue(IngameScript.Program.DbgLabel.RefPriority, out var rp) && rp.Calls > 0,
                "RefPriority (queue-derived ore walk) must have fired");
            Assert.That(s.TryGetValue(IngameScript.Program.DbgLabel.RefFactors, out var rf) && rf.Calls > 0,
                "RefFactors (NonRefManifest copy+subtract) must have fired");
            Assert.That(IngameScript.Program.tick >= 60, Is.True, "must run past tick 60 for the scan seams");
            Assert.That(s.TryGetValue(IngameScript.Program.DbgLabel.RefScan, out var rs) && rs.Calls >= 1,
                "RefScan (refinery discovery scan) must have fired at tick%60");
            Assert.That(s.TryGetValue(IngameScript.Program.DbgLabel.AsmScan, out var asmScan) && asmScan.Calls >= 1,
                "AsmScan (assembler discovery scan) must have fired at tick%60");

            // bottleneck checks.
            // (a) the seam-wrapped calls in the report must be sane. The first
            //     call of each label includes JIT/static-init cold cost, so the
            //     bound there is generous (20ms); the steady-state number comes
            //     from the direct warm benchmark below, which the harness's
            //     slow pass cadence would otherwise hide (RefPriority recomputes
            //     once per updateCounter change, and in 130 ticks of fake
            //     inventory churn that happened only once).
            Assert.That(rp.TotalMs, Is.LessThan(20.0), $"RefPriority cold call must be <20ms, got {rp.TotalMs:F4}ms");
            Assert.That(rf.TotalMs, Is.LessThan(20.0), $"RefFactors cold call must be <20ms, got {rf.TotalMs:F4}ms");
            Assert.That(rs.AvgMs, Is.LessThan(20.0), $"RefScan avg must be <20ms, got {rs.AvgMs:F4}ms");
            Assert.That(asmScan.AvgMs, Is.LessThan(20.0), $"AsmScan avg must be <20ms, got {asmScan.AvgMs:F4}ms");

            // (b) direct warm benchmark of the queue-derived walk itself: the
            //     hottest new path (called once per tick in the real script).
            //     200 iterations after 20 warmup -> must stay sub-ms per call.
            var walk = typeof(IngameScript.Program).GetNestedType("RefineryMgr", BindingFlags.NonPublic)
                .GetMethod("computeQueueOrePriority", NF);
            var sw = new Stopwatch();
            for (int i = 0; i < 20; i++) walk.Invoke(null, null);
            int iters = 200;
            sw.Restart();
            for (int i = 0; i < iters; i++) walk.Invoke(null, null);
            sw.Stop();
            double walkAvgMs = sw.Elapsed.TotalMilliseconds / iters;
            Console.WriteLine($"direct walk benchmark: {walkAvgMs:F5}ms/call over {iters} warm iterations");
            TestContext.WriteLine($"direct walk benchmark: {walkAvgMs:F5}ms/call over {iters} warm iterations");
            Assert.That(walkAvgMs, Is.LessThan(1.0),
                $"queue-derived walk must be sub-ms/call warm, got {walkAvgMs:F5}ms");

            // Main still encloses everything and the stack is balanced
            if (s.TryGetValue(IngameScript.Program.DbgLabel.Main, out var main))
                foreach (var kvp in s)
                    if (kvp.Key != IngameScript.Program.DbgLabel.Main)
                        Assert.That(main.TotalMs, Is.GreaterThanOrEqualTo(kvp.Value.TotalMs),
                            $"Main must enclose {kvp.Key}");
            Assert.That(diag.StackDepth, Is.Zero, "enter/exit stack must be balanced");
        }

        /// <summary>Refinery fake with the terminal-block stubs the block loader
        /// and inventory pipeline require (faction/grid match, HasInventory,
        /// player access, WcPbAPI null, working).</summary>
        static (IMyRefinery, FakeInventory) MakeProfilingRefinery(IMyCubeGrid grid)
        {
            var input = new FakeInventory((MyFixedPoint)100.0);
            var output = new FakeInventory((MyFixedPoint)100.0);
            var refinery = A.Fake<IMyRefinery>();
            A.CallTo(() => refinery.InputInventory).Returns(input);
            A.CallTo(() => refinery.OutputInventory).Returns(output);
            A.CallTo(() => refinery.BlockDefinition).Returns((SerializableDefinitionId)LargeRefineryDef);
            A.CallTo(() => refinery.Enabled).Returns(true);
            A.CallTo(() => refinery.CustomName).Returns("Refinery");
            A.CallTo(() => refinery.IsProducing).Returns(false);
            A.CallTo(() => refinery.UseConveyorSystem).Returns(false);
            A.CallTo(() => refinery.InventoryCount).Returns(2);
            A.CallTo(() => refinery.GetInventory(0)).Returns(input);
            A.CallTo(() => refinery.GetInventory(1)).Returns(output);
            A.CallTo(() => refinery.GetInventory(A<int>.That.Matches(i => i != 0 && i != 1))).Returns(null);
            A.CallTo(() => refinery.CubeGrid).Returns(grid);
            A.CallTo(() => refinery.GetOwnerFactionTag()).Returns("FACTION");
            A.CallTo(() => refinery.HasInventory).Returns(true);
            A.CallTo(() => refinery.HasPlayerAccess(A<long>.Ignored)).Returns(true);
            A.CallTo(() => refinery.IsWorking).Returns(true);
            A.CallTo(() => refinery.IsFunctional).Returns(true);
            A.CallTo(() => refinery.IsSameConstructAs(A<IMyTerminalBlock>.Ignored)).Returns(true);
            A.CallTo(() => refinery.EntityId).Returns(5001L);
            A.CallTo(() => refinery.GetProperty("WcPbAPI")).Returns(null);
            return (refinery, input);
        }

        /// <summary>Assembler fake with captured mode/queue state (same shape
        /// as AsmDiscoverTests.MakeAssembler) plus loader/pipeline stubs.</summary>
        static (IMyAssembler, AsmState2) MakeProfilingAssembler(IMyCubeGrid grid)
        {
            var input = new FakeInventory((MyFixedPoint)5.0);
            var output = new FakeInventory((MyFixedPoint)5.0);
            var state = new AsmState2();
            var asm = A.Fake<IMyAssembler>();
            A.CallTo(() => asm.InputInventory).Returns(input);
            A.CallTo(() => asm.OutputInventory).Returns(output);
            A.CallTo(() => asm.BlockDefinition).Returns((SerializableDefinitionId)new MyDefinitionId(typeof(MyObjectBuilder_Assembler), "LargeAssembler"));
            A.CallTo(() => asm.Enabled).Returns(true);
            A.CallTo(() => asm.CustomName).Returns("Assembler");
            A.CallTo(() => asm.IsProducing).Returns(false);
            A.CallTo(() => asm.IsQueueEmpty).ReturnsLazily(() => state.Queue.Count == 0);
            A.CallTo(() => asm.CanUseBlueprint(A<MyDefinitionId>.Ignored)).Returns(true);
            A.CallTo(() => asm.Mode).ReturnsLazily(() => state.Mode);
            A.CallToSet(() => asm.Mode).Invokes((MyAssemblerMode m) => state.Mode = m);
            A.CallTo(() => asm.UseConveyorSystem).ReturnsLazily(() => state.UseConv);
            A.CallToSet(() => asm.UseConveyorSystem).Invokes((bool v) => state.UseConv = v);
            A.CallTo(() => asm.ClearQueue()).Invokes(() => state.Queue.Clear());
            A.CallTo(() => asm.AddQueueItem(A<MyDefinitionId>.Ignored, A<MyFixedPoint>.Ignored))
                .Invokes((MyDefinitionId bp, MyFixedPoint amt) => state.Queue.Add(new MyProductionItem(0, bp, amt)));
            A.CallTo(() => asm.GetQueue(A<List<MyProductionItem>>.Ignored))
                .Invokes((List<MyProductionItem> q) => { q.Clear(); q.AddRange(state.Queue); });
            A.CallTo(() => asm.InventoryCount).Returns(2);
            A.CallTo(() => asm.GetInventory(0)).Returns(input);
            A.CallTo(() => asm.GetInventory(1)).Returns(output);
            A.CallTo(() => asm.GetInventory(A<int>.That.Matches(i => i != 0 && i != 1))).Returns(null);
            A.CallTo(() => asm.CubeGrid).Returns(grid);
            A.CallTo(() => asm.GetOwnerFactionTag()).Returns("FACTION");
            A.CallTo(() => asm.HasInventory).Returns(true);
            A.CallTo(() => asm.HasPlayerAccess(A<long>.Ignored)).Returns(true);
            A.CallTo(() => asm.IsWorking).Returns(true);
            A.CallTo(() => asm.IsFunctional).Returns(true);
            A.CallTo(() => asm.IsSameConstructAs(A<IMyTerminalBlock>.Ignored)).Returns(true);
            A.CallTo(() => asm.EntityId).Returns(5002L);
            A.CallTo(() => asm.GetProperty("WcPbAPI")).Returns(null);
            return (asm, state);
        }

        class AsmState2
        {
            public MyAssemblerMode Mode = MyAssemblerMode.Assembly;
            public bool UseConv = false;
            public List<MyProductionItem> Queue = new List<MyProductionItem>();
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
