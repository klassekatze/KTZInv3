using System.Collections.Generic;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// Special-container behavior:
    ///   - lacking items: the container declares stocktargets in CustomData and
    ///     is missing some -> sort_retrieve pulls from lower-priority containers
    ///   - excess items: holds more than the declared target -> expel pushes the
    ///     surplus to a category acceptor
    ///   - unwanted items: holds types NOT in its stocktargets -> expel removes them
    ///
    /// Also pins the mock's CustomData round-trip: the script READS stocktargets
    /// from it and WRITES to it (ISYCOMPAT header prefix), so the fake must have
    /// a real getter+setter.
    /// </summary>
    [TestFixture]
    public class SpecialContainerTests
    {
        static readonly MyItemType SteelPlate = new MyItemType("MyObjectBuilder_Component", "SteelPlate");
        static readonly MyItemType Motor = new MyItemType("MyObjectBuilder_Component", "Motor");

        IngameScript.Program _program;

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ScriptRunner.ResetStatics();
            _program = Gateway.CreateProgram().Build();
            IngameScript.Program.APIWC = new IngameScript.WcPbApi();
            IngameScript.Program.tick = 0;
            var pType = typeof(IngameScript.Program);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            SetStatic(pType, flags, "gAssemblerMgr");
            SetStatic(pType, flags, "gRefineryMgr");
            SetStatic(pType, flags, "gAutocraft");
            SetStatic(pType, flags, "gReactorMgr");
            var gInvField = pType.GetField("gInv", flags);
            if (gInvField != null)
                gInvField.SetValue(null, new IngameScript.Program.Inventory());
        }

        static void SetStatic(System.Type pType, System.Reflection.BindingFlags flags, string fieldName)
        {
            var field = pType.GetField(fieldName, flags);
            if (field == null) return;
            var ctor = field.FieldType.GetConstructor(System.Type.EmptyTypes);
            if (ctor == null) return;
            field.SetValue(null, ctor.Invoke(null));
        }

        IngameScript.Program.Inventory RunPipeline(List<IMyTerminalBlock> blocks, int ticks = 600)
        {
            var inv = new IngameScript.Program.Inventory();
            inv.updateContainers(blocks);
            for (int i = 0; i < ticks; i++)
            {
                IngameScript.Program.tick++;
                inv.update();
            }
            return inv;
        }

        // CustomData format: "TypeId/Subtype=count" WITHOUT the MyObjectBuilder_
        // prefix (the script adds bpprefix when parsing, exactly like ISY's own
        // auto-generated special-container files).
        const string SpecialCd = "Component/SteelPlate=500\n";

        [Test]
        public void Special_LackingItems_PullsFromLowerPriority()
        {
            // special declares SteelPlate=500 but holds none; a lower-priority
            // container holds the steel plates -> sort_retrieve must pull them in
            var special = CargoFactory.CreateCargo("Special [special] [P50]", SpecialCd,
                (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)0));
            var donor = CargoFactory.CreateCargo("Donor [Components] [P500]",
                (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)500));

            RunPipeline(new List<IMyTerminalBlock> { special.Block, donor.Block });

            Assert.That((double)special.AmountOf(SteelPlate), Is.EqualTo(500.0),
                "special must pull its declared stocktarget from the lower-priority donor");
            Assert.That((double)donor.AmountOf(SteelPlate), Is.EqualTo(0.0),
                "donor must have given up all of the special's stocktarget");
        }

        [Test]
        public void Special_Excess_ExpelsSurplusToAcceptor()
        {
            // special declares SteelPlate=500 but holds 1200 -> the 700 surplus
            // must be expelled to a container accepting Components
            var special = CargoFactory.CreateCargo("Special [special] [P50]", SpecialCd,
                (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)1200));
            var acceptor = CargoFactory.CreateCargo("Acceptor [Components] [P500]",
                (MyFixedPoint)10.0);

            RunPipeline(new List<IMyTerminalBlock> { special.Block, acceptor.Block });

            Assert.That((double)special.AmountOf(SteelPlate), Is.EqualTo(500.0),
                "special must keep exactly its declared stocktarget");
            Assert.That((double)acceptor.AmountOf(SteelPlate), Is.EqualTo(700.0),
                "surplus must have been expelled to the category acceptor");
        }

        [Test]
        public void Special_UnwantedItems_Expelled()
        {
            // special declares only SteelPlate but holds Motors -> the motors are
            // not stocktargets and must be expelled entirely
            var special = CargoFactory.CreateCargo("Special [special] [P50]", SpecialCd,
                (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)500), (Motor, (MyFixedPoint)100));
            var acceptor = CargoFactory.CreateCargo("Acceptor [Components] [P500]",
                (MyFixedPoint)10.0);

            RunPipeline(new List<IMyTerminalBlock> { special.Block, acceptor.Block });

            Assert.That((double)special.AmountOf(Motor), Is.EqualTo(0.0),
                "unwanted (undeclared) items must be expelled from the special");
            Assert.That((double)special.AmountOf(SteelPlate), Is.EqualTo(500.0),
                "declared stocktarget must remain untouched");
            Assert.That((double)acceptor.AmountOf(Motor), Is.EqualTo(100.0),
                "expelled motors must land in the category acceptor");
        }

        [Test]
        public void Mock_CustomData_IsReadableAndWritable()
        {
            // the script reads stocktargets from CustomData (proven by the other
            // tests) and WRITES to it: the ISYCOMPAT header is prefixed on first
            // updateP. The fake must return the written value (real setter).
            var special = CargoFactory.CreateCargo("Special [special] [P50]", SpecialCd,
                (MyFixedPoint)10.0);
            var donor = CargoFactory.CreateCargo("Donor [Components] [P500]",
                (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)500));

            RunPipeline(new List<IMyTerminalBlock> { special.Block, donor.Block });

            var written = special.Block.CustomData;
            Assert.That(written, Does.Contain("Special Container modes:"),
                "ISYCOMPAT must have prefixed the header into CustomData (proves the setter works)");
            Assert.That(written, Does.Contain("Component/SteelPlate=500"),
                "the original stocktarget line must survive the prefix");
            // and the validator must agree with the script on the parsed targets
            Assert.That((double)special.AmountOf(SteelPlate), Is.EqualTo(500.0),
                "stocktargets parsed from the (prefixed) CustomData must still work");
        }

        [Test]
        public void Special_AfterRun_ValidatorClean()
        {
            var special = CargoFactory.CreateCargo("Special [special] [P50]", SpecialCd,
                (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)1200), (Motor, (MyFixedPoint)100));
            var acceptor = CargoFactory.CreateCargo("Acceptor [Components] [P500]",
                (MyFixedPoint)10.0);
            var grid = CargoFactory.CreateGrid();
            var world = Wrap(grid, special, acceptor);

            var runner = ScriptRunner.Create(world.Gts, world.Me);
            Assert.That(runner.RunUntilUpdateCounter(2), Is.True,
                $"updateCounter 2 not reached (used {runner.TicksUsed} ticks)");

            var violations = InventoryValidator.Validate(world);
            foreach (var v in violations) TestContext.WriteLine(v.ToString());
            Assert.That(violations, Is.Empty,
                $"special container must be validator-clean after sorting: {string.Join("\n", violations)}");
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
