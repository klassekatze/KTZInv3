using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// Stress test: 100 containers with an ENTIRELY random labeling composition
    /// (random category tokens, priorities, special/locked/hidden/alltypes
    /// variants, random CustomData stocktargets for specials), half of them
    /// filled with random items and counts. Runs the full script for 10 complete
    /// inventory passes, then runs the brute-force InventoryValidator against
    /// every container: the final state must have no misplaced items.
    ///
    /// Seeded RNG (not crypto-random) so failures are reproducible.
    /// </summary>
    [TestFixture]
    public class RandomSortStressTests
    {
        // item pool: registered types covering all six categories
        static readonly MyItemType SteelPlate = new MyItemType("MyObjectBuilder_Component", "SteelPlate");
        static readonly MyItemType Construction = new MyItemType("MyObjectBuilder_Component", "ConstructionComponent");
        static readonly MyItemType InteriorPlate = new MyItemType("MyObjectBuilder_Component", "InteriorPlate");
        static readonly MyItemType Motor = new MyItemType("MyObjectBuilder_Component", "Motor");
        static readonly MyItemType LargeTube = new MyItemType("MyObjectBuilder_Component", "LargeTube");
        static readonly MyItemType Stone = new MyItemType("MyObjectBuilder_Ore", "Stone");
        static readonly MyItemType IronOre = new MyItemType("MyObjectBuilder_Ore", "Iron");
        static readonly MyItemType IronIngot = new MyItemType("MyObjectBuilder_Ingot", "Iron");
        static readonly MyItemType[] AllItems = {
            SteelPlate, Construction, InteriorPlate, Motor, LargeTube,
            Stone, IronOre, IronIngot,
        };

        static readonly string[] CategoryTokens = { "Ores", "Ingots", "Components", "Ammo", "Bottles", "Tools" };

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ScriptRunner.ResetStatics();
        }

        [Test]
        [TestCase(12345)]
        [TestCase(98765)]
        [TestCase(424242)]
        [TestCase(777001)]
        public void Random100Containers_10Passes_ValidatorClean(int seed)
        {
            var (world, cargos) = BuildRandomWorld(seed, 100);

            var runner = ScriptRunner.Create(world.Gts, world.Me);
            // 100 containers x 3 steps x (5-15 tick interval) per pass -> 10 passes
            // can take tens of thousands of ticks; give it a generous budget
            const int MaxTicks = 200000;
            Assert.That(runner.RunUntilUpdateCounter(10, MaxTicks), Is.True,
                $"updateCounter 10 not reached after {MaxTicks} ticks (used {runner.TicksUsed})");
            TestContext.WriteLine($"ticks={runner.TicksUsed} updateCounter={runner.GetGInv()?.updateCounter}");

            // the run must have actually MOVED items - otherwise "no violations"
            // is trivially true. transfer_count is a static that counts successful
            // TransferItemTo calls and never resets ("xfer ops this runtime").
            var tf = typeof(IngameScript.Program.Inventory).GetField("transfer_count",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var transferOps = (int)(tf?.GetValue(null) ?? 0);
            TestContext.WriteLine($"total transfer ops in run: {transferOps}");
            Assert.That(transferOps, Is.GreaterThan(0),
                "the run must have performed real transfers, not just sat there");

            // brute-force: no item may have a better home, nothing may sit in a
            // container that doesn't accept its category, specials may only hold
            // declared stocktargets
            var violations = InventoryValidator.Validate(world);
            foreach (var v in violations.Take(30)) TestContext.WriteLine(v.ToString());
            Assert.That(violations, Is.Empty,
                $"expected zero violations after 10 passes over 100 containers, " +
                $"got {violations.Count}: {string.Join("\n", violations.Take(10))}");
        }

        /// <summary>
        /// Profiles the same random 100-container composition through the Diag
        /// seam: per-label cost of 10 full inventory passes. The point is to see
        /// whether per-container cost scales sanely at 100 blocks (vs the 44-block
        /// blueprint) and whether any label is doing disproportionate work.
        /// </summary>
        [Test]
        public void Random100Containers_ProfilePerLabelCost()
        {
            var (world, _) = BuildRandomWorld(12345, 100);

            IngameScript.Program.DEBUGGING = true;
            var diag = new TimingDiag();
            IngameScript.Program.diag = diag;

            var runner = ScriptRunner.Create(world.Gts, world.Me);
            const int MaxTicks = 200000;
            Assert.That(runner.RunUntilUpdateCounter(10, MaxTicks), Is.True,
                $"updateCounter 10 not reached after {MaxTicks} ticks (used {runner.TicksUsed})");

            var report = diag.Report($"random 100 containers, ticks={runner.TicksUsed}, 10 passes");
            TestContext.WriteLine("\n" + report);
            Console.WriteLine("\n===== random-100 profile =====");
            Console.WriteLine(report);
            Console.WriteLine("==============================");

            // ---- evaluate ----
            Assert.That(diag.StackDepth, Is.Zero, "enter/exit stack must be balanced");
            Assert.That(diag.Stats.TryGetValue(IngameScript.Program.DbgLabel.InvBlocks, out var invu), Is.True,
                "InvBlocks must have fired");
            // red-flag threshold: per-block inv work must stay sub-millisecond at
            // 100 containers (the blueprint world stays ~0.04ms/block)
            Assert.That(invu.AvgMs, Is.LessThan(1.0),
                $"per-block inv update should be <1ms even at 100 containers, got {invu.AvgMs:F4}ms");
            TestContext.WriteLine($"InvBlocks: calls={invu.Calls} total={invu.TotalMs:F2}ms avg={invu.AvgMs:F4}ms max={invu.MaxMs:F4}ms");
        }

        /// <summary>Builds a world of <paramref name="containerCount"/> containers
        /// with a seeded-random labeling composition, half of them filled with
        /// random items and counts.</summary>
        static (BlueprintFactory.World world, List<CargoMock> cargos) BuildRandomWorld(int seed, int containerCount)
        {
            var rng = new Random(seed);

            var grid = CargoFactory.CreateGrid();
            var cargos = new List<CargoMock>();

            for (int i = 0; i < containerCount; i++)
            {
                var name = RandomLabel(rng, i);
                var customData = name.Contains("special") ? RandomCustomData(rng) : null;
                var maxVol = (MyFixedPoint)rng.Next(5, 50); // 5..50 m^3

                // fill HALF the containers with random items and counts
                var items = new List<(MyItemType, MyFixedPoint)>();
                if (i % 2 == 0)
                {
                    int count = rng.Next(1, 6);
                    for (int k = 0; k < count; k++)
                    {
                        var type = AllItems[rng.Next(AllItems.Length)];
                        var amount = (MyFixedPoint)rng.Next(10, 5000);
                        items.Add((type, amount));
                    }
                }

                var cargo = customData == null
                    ? CargoFactory.CreateCargo(name, maxVol, grid, items.ToArray())
                    : CargoFactory.CreateCargo(name, customData, maxVol, items.ToArray());
                cargos.Add(cargo);
            }

            // sanity: all six categories appear somewhere, and some specials exist
            var allNames = string.Join(" ", cargos.Select(c => c.Block.CustomName));
            foreach (var cat in CategoryTokens)
                Assert.That(allNames.ToLower(), Does.Contain(cat.ToLower()),
                    $"category '{cat}' must appear in the random composition");
            Assert.That(allNames, Does.Contain("special"),
                "some containers must be special for this stress test to cover them");

            var gts = new FakeGts();
            foreach (var c in cargos) gts.Blocks.Add(c.Block);
            var world = new BlueprintFactory.World
            {
                Gts = gts,
                Me = MeFactory.CreateMe(grid),
                Grids = { grid },
            };
            foreach (var c in cargos)
            {
                world.Cargos.Add(c);
                world.BlueprintCargos.Add(new BlueprintCargo
                {
                    Name = c.Block.CustomName,
                    EntityId = 0,
                    MaxVolume = c.AsFakeInventory().MaxVolume,
                });
            }
            return (world, cargos);
        }

        /// <summary>Random label: 0-3 category tokens, random priority, and
        /// occasionally a special/locked/hidden/alltypes modifier.</summary>
        static string RandomLabel(Random rng, int index)
        {
            var parts = new List<string> { "Cargo" + index };

            // 0-3 random category tokens
            var cats = CategoryTokens.OrderBy(_ => rng.Next()).Take(rng.Next(0, 4)).ToList();
            parts.AddRange(cats);

            // random modifier: special/locked/hidden/alltypes ~15% each
            int roll = rng.Next(100);
            if (roll < 15) parts.Add("special");
            else if (roll < 25) parts.Add("locked");
            else if (roll < 32) parts.Add("hidden");
            else if (roll < 42) parts.Add("alltypes");

            // random priority 10..999
            parts.Add("P" + rng.Next(10, 1000));

            return string.Join(" ", parts);
        }

        /// <summary>Random stocktarget lines for a special container: 1-3 random
        /// item types with random counts (ISY format: TypeId/Subtype=count).</summary>
        static string RandomCustomData(Random rng)
        {
            var lines = new List<string>();
            int n = rng.Next(1, 4);
            for (int i = 0; i < n; i++)
            {
                var type = AllItems[rng.Next(AllItems.Length)];
                // strip the MyObjectBuilder_ prefix like the game's own special files
                var typeId = type.TypeId.StartsWith("MyObjectBuilder_")
                    ? type.TypeId.Substring("MyObjectBuilder_".Length)
                    : type.TypeId;
                lines.Add($"{typeId}/{type.SubtypeId}={rng.Next(100, 5000)}");
            }
            return string.Join("\n", lines) + "\n";
        }
    }
}
