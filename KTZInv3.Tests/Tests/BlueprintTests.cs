using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// Full-script test driven by a REAL blueprint file: parses the .sbc,
    /// mocks every cargo container with its exact name and inventory contents,
    /// then runs Program.Main() until gInv.updateCounter advances and checks
    /// whether the [P999] ship cargos' contents flow to the [P99] base cargos.
    ///
    /// Blueprint location: override with env var KTZINV3_BLUEPRINT, otherwise
    /// TestData/DockedTest.sbc in the repo (gitignored, *.sbc).
    /// </summary>
    [TestFixture]
    public class BlueprintTests
    {
        static readonly MyItemType IronOre = new MyItemType("MyObjectBuilder_Ore", "Iron");
        static readonly MyItemType Stone = new MyItemType("MyObjectBuilder_Ore", "Stone");
        static readonly MyItemType IronIngot = new MyItemType("MyObjectBuilder_Ingot", "Iron");

        static string BlueprintPath
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("KTZINV3_BLUEPRINT");
                if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
                var local = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "TestData", "DockedTest.sbc");
                if (File.Exists(local)) return Path.GetFullPath(local);
                // fallback: the exact file the user sent
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

        [Test]
        public void Blueprint_ParsesAllCargos()
        {
            var world = BlueprintFactory.Load(BlueprintPath);
            Assert.That(world.Cargos.Count, Is.GreaterThan(0), "blueprint must contain cargo containers");
            Assert.That(world.Grids.Count, Is.GreaterThanOrEqualTo(2), "blueprint should have ship + base grids");
            Assert.That(world.BlueprintCargos.Any(c => c.Name.Contains("[P999]")), Is.True, "ship cargos with [P999] expected");
            Assert.That(world.BlueprintCargos.Any(c => c.Name.Contains("[P99]")), Is.True, "base cargos with [P99] expected");
            Assert.That(world.BlueprintCargos.Any(c => c.Items.Count > 0), Is.True, "some cargos must contain items");
        }

        [Test]
        public void Blueprint_ShipP999_FlowsTo_BaseP99()
        {
            var world = BlueprintFactory.Load(BlueprintPath);

            // total contents of [P999] cargos before the run
            var p999Before = TotalAmount(world, "[P999]");
            var p99Before = TotalAmount(world, "[P99]");
            Assert.That((double)p999Before, Is.GreaterThan(0.0), "[P999] cargos must start with items");

            var runner = ScriptRunner.Create(world.Gts, world.Me);
            Assert.That(runner.RunUntilUpdateCounter(2), Is.True,
                $"updateCounter 2 not reached after {ScriptRunner.MaxTicks} ticks (used {runner.TicksUsed})");

            var p999After = TotalAmount(world, "[P999]");
            var p99After = TotalAmount(world, "[P99]");

            TestContext.WriteLine($"ticks={runner.TicksUsed} counter={runner.GetGInv()?.updateCounter}");
            TestContext.WriteLine($"P999 before={p999Before} after={p999After}");
            TestContext.WriteLine($"P99  before={p99Before} after={p99After}");
            DumpCargos(world, "[P999]");
            DumpCargos(world, "[P99]");

            Assert.That((double)p999After, Is.LessThan((double)p999Before),
                "[P999] ship cargos should have given up items to the [P99] base cargos");
            Assert.That((double)p99After, Is.GreaterThan((double)p99Before),
                "[P99] base cargos should have received items");
        }

        [Test]
        public void Blueprint_ByType_P999ToP99_AlltypesAndOresIngots()
        {
            var world = BlueprintFactory.Load(BlueprintPath);

            var shipIron = TotalOf(world, "[P999]", IronOre) + TotalOf(world, "[P999]", IronIngot);
            Assert.That((double)shipIron, Is.GreaterThan(0.0), "ship should hold iron ore/ingot to test");

            var runner = ScriptRunner.Create(world.Gts, world.Me);
            runner.RunUntilUpdateCounter(2);

            var shipIronAfter = TotalOf(world, "[P999]", IronOre) + TotalOf(world, "[P999]", IronIngot);
            var baseIronAfter = TotalOf(world, "[P99]", IronOre) + TotalOf(world, "[P99]", IronIngot);
            TestContext.WriteLine($"ship iron: before={shipIron} after={shipIronAfter}");
            TestContext.WriteLine($"base iron: after={baseIronAfter}");

            Assert.That((double)shipIronAfter, Is.LessThan((double)shipIron),
                "ship [P999] should export iron (ore/ingot) to the [P99] base");
        }

        // ---- helpers -------------------------------------------------------

        static MyFixedPoint TotalAmount(BlueprintFactory.World world, string nameToken)
        {
            MyFixedPoint sum = 0;
            for (int i = 0; i < world.Cargos.Count; i++)
                if (world.BlueprintCargos[i].Name.Contains(nameToken))
                    sum += world.Cargos[i].AsFakeInventory().TotalAmount();
            return sum;
        }

        static MyFixedPoint TotalOf(BlueprintFactory.World world, string nameToken, MyItemType type)
        {
            MyFixedPoint sum = 0;
            for (int i = 0; i < world.Cargos.Count; i++)
                if (world.BlueprintCargos[i].Name.Contains(nameToken))
                    sum += world.Cargos[i].AsFakeInventory().AmountOf(type);
            return sum;
        }

        static void DumpCargos(BlueprintFactory.World world, string nameToken)
        {
            foreach (var c in world.BlueprintCargos)
            {
                if (!c.Name.Contains(nameToken)) continue;
                var contents = string.Join(", ", c.Items.Select(i => $"{i.type.SubtypeId}:{i.amount}"));
                TestContext.WriteLine($"  {c.Name} vol={c.MaxVolume} items=[{contents}]");
            }
        }
    }
}
