using System.Collections.Generic;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// Exercises the KTZInv3 Inventory sorting machinery (the same code that runs
    /// on the server) against mocked cargo containers. Every test prepares its
    /// fake inventory via <see cref="CargoFactory.CreateCargo"/> and drives the
    /// real update pipeline: updateM (manifest) -> updateP (parse name/priority)
    /// -> updateT (transfer).
    /// </summary>
    [TestFixture]
    public class InventorySortTests
    {
        static readonly MyItemType SteelPlate = new MyItemType("MyObjectBuilder_Component", "SteelPlate");
        static readonly MyItemType Motor = new MyItemType("MyObjectBuilder_Component", "Motor");
        static readonly MyItemType IronOre = new MyItemType("MyObjectBuilder_Ore", "Iron");

        IngameScript.Program _program;

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ResetStatics();
            _program = Gateway.CreateProgram().Build();
            IngameScript.Program.APIWC = new IngameScript.WcPbApi(); // HasCoreWeapon -> false, avoids NRE
            IngameScript.Program.tick = 0;
            // genstatus() dereferences these statics; the real game sets them in
            // main() before gInv.update() runs. They're private, so set via reflection.
            var pType = typeof(IngameScript.Program);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            SetStatic(pType, flags, "gAssemblerMgr");
            SetStatic(pType, flags, "gRefineryMgr");
            SetStatic(pType, flags, "gAutocraft");
            SetStatic(pType, flags, "gReactorMgr");
            // gInv is the Inventory instance itself; expel()/transfer_item() call
            // gInv.rerrlog() when a transfer fails.
            var gInvField = pType.GetField("gInv", flags);
            if (gInvField != null)
                gInvField.SetValue(null, new IngameScript.Program.Inventory());
        }

        /// <summary>Instantiates the private nested manager class and assigns it to the static field.</summary>
        static void SetStatic(System.Type pType, System.Reflection.BindingFlags flags, string fieldName)
        {
            var field = pType.GetField(fieldName, flags);
            if (field == null) return;
            var ctor = field.FieldType.GetConstructor(System.Type.EmptyTypes);
            if (ctor == null) return;
            field.SetValue(null, ctor.Invoke(null));
        }

        static void ResetStatics()
        {
            IngameScript.Program.Inventory.globalManifest.stuff.Clear();
            IngameScript.Program.Inventory.globalManifest.maxVolume = 0;
            IngameScript.Program.Inventory.globalManifest.freeVolume = 0;
            IngameScript.Program.Inventory.globalManifest.typeVolume.Clear();
            IngameScript.Program.Inventory.encounteredTypes.Clear();
            IngameScript.Program.Inventory.nonFractionalMinMarginByCat.Clear();
            IngameScript.Program.Inventory.prAggs.Clear();
            IngameScript.Program.Inventory.BlockInventory.bPriorityList.Clear();
            IngameScript.Program.Inventory.BlockInventory.bIDict.Clear();
            IngameScript.Program.Inventory.BlockInventory.idl = 0;
        }

        /// <summary>
        /// Builds an Inventory, feeds it the given blocks, and runs the update
        /// pipeline for the given number of ticks (the per-block steps are gated
        /// on tick intervals, so a full pass takes a while).
        /// </summary>
        IngameScript.Program.Inventory RunPipeline(List<IMyTerminalBlock> blocks, int ticks = 300)
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

        [Test]
        public void LowerPrioritySource_PushesToHigherPriorityEmptyReceiver()
        {
            // The user's exact scenario: source has the category at p999, the
            // receiver is COMPLETELY EMPTY with the same category at p99.
            var source = CargoFactory.CreateCargo("2 CCTT Cargo [Components].P999",
                (MyFixedPoint)5.0, (SteelPlate, (MyFixedPoint)1000));
            var receiver = CargoFactory.CreateCargo("1 Nascent Cargo [Components].P99",
                (MyFixedPoint)5.0);

            RunPipeline(new List<IMyTerminalBlock> { source.Block, receiver.Block });

            Assert.That((double)receiver.AmountOf(SteelPlate), Is.GreaterThan(0.0),
                "items should have been pushed from p999 source into the empty p99 receiver");
            Assert.That((double)source.AmountOf(SteelPlate), Is.LessThan(1000.0),
                "source should have given up items");
        }

        [Test]
        public void EmptyReceiver_ReceivesFullTransferUpToCapacity()
        {
            // The count-vs-volume fix: a completely empty receiver with 1.0 m^3
            // free must receive items by count (volume / itemVolume), not be
            // clamped to "freeVolume as count".
            var source = CargoFactory.CreateCargo("2 CCTT Cargo [Components].P999",
                (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)5000));
            var receiver = CargoFactory.CreateCargo("1 Nascent Cargo [Components].P99",
                (MyFixedPoint)1.0); // 1 m^3 = 10,000 steel plates at 0.0001 m^3

            RunPipeline(new List<IMyTerminalBlock> { source.Block, receiver.Block });

            Assert.That((double)receiver.AmountOf(SteelPlate), Is.GreaterThan(1000.0),
                "receiver should be filled by volume-based count, not raw free-volume number");
        }

        [Test]
        public void ReceiverWithoutCategory_ItemsFlowToNextCategorizedContainer()
        {
            // The user's observed diagnostic: rename the p99 receiver so it has NO
            // valid category -> items immediately flow to the other container.
            var source = CargoFactory.CreateCargo("2 CCTT Cargo [Components].P999",
                (MyFixedPoint)5.0, (SteelPlate, (MyFixedPoint)1000));
            var renamedReceiver = CargoFactory.CreateCargo("1 Nascent Cargo.P99", // no category token
                (MyFixedPoint)5.0);
            var other = CargoFactory.CreateCargo("3 Overflow Cargo [Components].P500",
                (MyFixedPoint)5.0);

            RunPipeline(new List<IMyTerminalBlock> { source.Block, renamedReceiver.Block, other.Block });

            Assert.That((double)renamedReceiver.AmountOf(SteelPlate), Is.EqualTo(0.0),
                "receiver without the category must not receive components");
            Assert.That((double)other.AmountOf(SteelPlate), Is.GreaterThan(0.0),
                "items should have flowed to the other categorized container");
        }

        [Test]
        public void DeadEndReceiver_DoesNotBlockOtherDestinations()
        {
            // THE REMAINING ROOT ISSUE: if the highest-priority receiver can't
            // actually accept items (e.g. no conveyor connection), the push loop
            // retries it 10x and aborts — the whole category is blocked and the
            // fallback container gets nothing. This test pins the expected
            // behavior: flow must continue to the next candidate.
            var source = CargoFactory.CreateCargo("2 CCTT Cargo [Components].P999",
                (MyFixedPoint)5.0, (SteelPlate, (MyFixedPoint)1000));

            var deadEnd = CargoFactory.CreateCargo("1 Nascent Cargo [Components].P99",
                (MyFixedPoint)5.0);
            deadEnd.AsFakeInventory().ConveyorConnected = false; // no conveyor path

            var fallback = CargoFactory.CreateCargo("3 Overflow Cargo [Components].P500",
                (MyFixedPoint)5.0);

            RunPipeline(new List<IMyTerminalBlock> { source.Block, deadEnd.Block, fallback.Block });

            Assert.That((double)fallback.AmountOf(SteelPlate), Is.GreaterThan(0.0),
                "a dead-end receiver must not starve the fallback container");
        }

        [Test]
        public void Expel_MovesItemsOutOfUncategorizedSource()
        {
            // Source WITHOUT the category: expel kicks in and pushes to any
            // categorized container.
            var source = CargoFactory.CreateCargo("2 CCTT Cargo.P999",
                (MyFixedPoint)5.0, (SteelPlate, (MyFixedPoint)100));
            var receiver = CargoFactory.CreateCargo("1 Nascent Cargo [Components].P99",
                (MyFixedPoint)5.0);

            RunPipeline(new List<IMyTerminalBlock> { source.Block, receiver.Block });

            Assert.That((double)receiver.AmountOf(SteelPlate), Is.GreaterThan(0.0),
                "expel should push items from the uncategorized source into the categorized container");
        }
    }
}
