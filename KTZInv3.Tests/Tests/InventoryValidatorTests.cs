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
    [TestFixture]
    public class InventoryValidatorTests
    {
        static readonly MyItemType IronOre = new MyItemType("MyObjectBuilder_Ore", "Iron");
        static readonly MyItemType SteelPlate = new MyItemType("MyObjectBuilder_Component", "SteelPlate");

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ScriptRunner.ResetStatics();
        }

        static string BlueprintPath
        {
            get
            {
                var local = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "TestData", "DockedTest.sbc");
                if (File.Exists(local)) return Path.GetFullPath(local);
                return "/home/user/.hermes/cache/documents/doc_cdc2b2ad2319_bp.sbc";
            }
        }

        [Test]
        public void Blueprint_AfterRun_IsClean()
        {
            var world = BlueprintFactory.Load(BlueprintPath);
            var runner = ScriptRunner.Create(world.Gts, world.Me);
            runner.RunUntilUpdateCounter(2);

            var violations = InventoryValidator.Validate(world);
            foreach (var v in violations) TestContext.WriteLine(v.ToString());
            Assert.That(violations, Is.Empty,
                $"expected no misplaced items after a settled run, got {violations.Count}: {string.Join("\n", violations)}");
        }

        [Test]
        public void Blueprint_BeforeRun_HasMisplacedShipItems()
        {
            // BEFORE the script runs, the [P999] ship cargos hold ores/ingots that
            // the higher-priority [P99] base cargos accept -> validator must flag them.
            var world = BlueprintFactory.Load(BlueprintPath);
            var violations = InventoryValidator.Validate(world);

            Assert.That(violations, Is.Not.Empty, "ship items should be flagged as belonging in the base");
            foreach (var v in violations.Take(6)) TestContext.WriteLine(v.ToString());
            Assert.That(violations.Any(v => v.Reason.Contains("priority")), Is.True,
                "at least one violation must cite a higher-priority destination");
        }

        [Test]
        public void Manual_IsolatedGrid_NoViolations()
        {
            // one container only: nothing higher to move to, nothing wrong.
            var grid = CargoFactory.CreateGrid();
            var cargo = CargoFactory.CreateCargo("P500 Test Cargo [Ores] [P500]",
                (MyFixedPoint)1000, grid, (IronOre, (MyFixedPoint)50));
            var world = Wrap(grid, cargo);

            var violations = InventoryValidator.Validate(world);
            Assert.That(violations, Is.Empty);
        }

        [Test]
        public void Manual_LowerPriority_Source_ShouldMoveToHigher()
        {
            // P999 holds ore, P99 accepts Ores with room -> violation expected.
            var grid = CargoFactory.CreateGrid();
            var low = CargoFactory.CreateCargo("Cargo Low [Ores] [P999]",
                (MyFixedPoint)1000, grid, (IronOre, (MyFixedPoint)50));
            var high = CargoFactory.CreateCargo("Cargo High [Ores] [P99]",
                (MyFixedPoint)1000, grid);
            var world = Wrap(grid, low, high);

            var violations = InventoryValidator.Validate(world);
            Assert.That(violations, Is.Not.Empty);
            var v = violations[0];
            TestContext.WriteLine(v.ToString());
            Assert.That(v.InContainer, Is.EqualTo("Cargo Low [Ores] [P999]"));
            Assert.That(v.Reason, Does.Contain("Cargo High [Ores] [P99]"));
        }

        [Test]
        public void Manual_HigherPriority_Full_NoViolation()
        {
            // P99 is completely full: no non-negligible space -> item correctly stays in P999.
            var grid = CargoFactory.CreateGrid();
            var low = CargoFactory.CreateCargo("Cargo Low [Ores] [P999]",
                (MyFixedPoint)1000, grid, (IronOre, (MyFixedPoint)50));
            // capacity 0.05 m^3 holds 135 ore (0.00037 m^3 each) - pack it full
            var high = CargoFactory.CreateCargo("Cargo High [Ores] [P99]",
                (MyFixedPoint)0.05, grid, (IronOre, (MyFixedPoint)135));
            var world = Wrap(grid, low, high);

            var violations = InventoryValidator.Validate(world);
            foreach (var v in violations) TestContext.WriteLine(v.ToString());
            Assert.That(violations, Is.Empty,
                "full higher-priority container must not attract the item (no non-negligible space)");
        }

        static BlueprintFactory.World Wrap(IMyCubeGrid grid, params CargoMock[] cargos)
        {
            var world = new BlueprintFactory.World();
            world.Gts = new FakeGts();
            world.Me = MeFactory.CreateMe(grid);
            world.Grids.Add(grid);
            foreach (var c in cargos)
            {
                world.Cargos.Add(c);
                world.Gts.Blocks.Add(c.Block);
                world.BlueprintCargos.Add(new BlueprintCargo
                {
                    Name = c.Block.CustomName,
                    EntityId = 0,
                    MaxVolume = c.AsFakeInventory().MaxVolume,
                });
            }
            return world;
        }
    }
}
