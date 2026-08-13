using System.Collections.Generic;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// FULL-SCRIPT tests: the Program instance is built via the Gateway, its
    /// GridTerminalSystem is a FakeGts holding mock cargo containers, and the
    /// test then calls Program.Main("", UpdateType.Update1) in a loop — exactly
    /// what the game engine does each tick — until gInv.updateCounter reaches
    /// the target. Only then is state evaluated.
    ///
    /// This exercises the entire real path: skipper -> hypermain -> main() ->
    /// ResourceLoader (11-step boot, gated on WcPbApi activation) -> Inventory
    /// creation -> updateM/updateP/updateT passes.
    /// </summary>
    [TestFixture]
    public class FullScriptTests
    {
        static readonly MyItemType SteelPlate = new MyItemType("MyObjectBuilder_Component", "SteelPlate");

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ScriptRunner.ResetStatics();
        }

        /// <summary>
        /// Standard 3-container dead-end scenario: source p999 with items,
        /// dead-end receiver p99 (no conveyor), fallback p500. Runs the full
        /// script until two complete inventory passes have happened.
        /// </summary>
        static ScriptRunner RunDeadEndScenario(out CargoMock source, out CargoMock deadEnd, out CargoMock fallback)
        {
            var grid = CargoFactory.CreateGrid();
            source = CargoFactory.CreateCargo("2 CCTT Cargo [Components].P999", (MyFixedPoint)5.0, grid, (SteelPlate, (MyFixedPoint)1000));
            deadEnd = CargoFactory.CreateCargo("1 Nascent Cargo [Components].P99", (MyFixedPoint)5.0, grid);
            deadEnd.AsFakeInventory().ConveyorConnected = false;
            fallback = CargoFactory.CreateCargo("3 Overflow Cargo [Components].P500", (MyFixedPoint)5.0, grid);

            var gts = new FakeGts();
            gts.Blocks.Add(source.Block);
            gts.Blocks.Add(deadEnd.Block);
            gts.Blocks.Add(fallback.Block);

            var me = MeFactory.CreateMe(grid);
            var runner = ScriptRunner.Create(gts, me);
            var reached = runner.RunUntilUpdateCounter(2);
            Assert.That(reached, Is.True,
                $"updateCounter 2 not reached after {ScriptRunner.MaxTicks} ticks (used {runner.TicksUsed})");
            return runner;
        }

        [Test]
        public void Boot_CompletesAndRunsInventoryPasses()
        {
            var grid = CargoFactory.CreateGrid();
            var cargo = CargoFactory.CreateCargo("1 Cargo [Components].P99", (MyFixedPoint)5.0, grid);

            var gts = new FakeGts();
            gts.Blocks.Add(cargo.Block);

            var runner = ScriptRunner.Create(gts, MeFactory.CreateMe(grid));
            Assert.That(runner.RunUntilUpdateCounter(1), Is.True,
                $"boot/1 pass not reached after {ScriptRunner.MaxTicks} ticks (used {runner.TicksUsed})");

            var inv = runner.GetGInv();
            Assert.That(inv, Is.Not.Null, "gInv must exist after boot");
            Assert.That(inv.hasUpdatedOnce, Is.True, "hasUpdatedOnce must be set after a full pass");
            Assert.That(inv.updateCounter, Is.GreaterThanOrEqualTo(1));
            Assert.That(runner.EchoMessages, Is.Not.Empty, "mocked Echo must capture the script's output");
        }

        [Test]
        public void FullScript_DeadEndReceiver_ItemsFlowToFallback()
        {
            var runner = RunDeadEndScenario(out var source, out var deadEnd, out var fallback);

            // The whole script ran for real: boot + two passes. The dead-end
            // receiver must not have starved the fallback container.
            Assert.That((double)fallback.AmountOf(SteelPlate), Is.GreaterThan(0.0),
                "fallback container must receive the steel plates through the full script run");
            Assert.That((double)deadEnd.AmountOf(SteelPlate), Is.EqualTo(0.0),
                "dead-end receiver must receive nothing");
            Assert.That((double)source.AmountOf(SteelPlate), Is.LessThan(1000.0),
                "source must have given up items");
        }

        [Test]
        public void FullScript_UpdateCounterKeepsAdvancing()
        {
            var grid = CargoFactory.CreateGrid();
            var source = CargoFactory.CreateCargo("2 CCTT Cargo [Components].P999", (MyFixedPoint)5.0, grid, (SteelPlate, (MyFixedPoint)500));
            var receiver = CargoFactory.CreateCargo("1 Nascent Cargo [Components].P99", (MyFixedPoint)5.0, grid);

            var gts = new FakeGts();
            gts.Blocks.Add(source.Block);
            gts.Blocks.Add(receiver.Block);

            var runner = ScriptRunner.Create(gts, MeFactory.CreateMe(grid));
            Assert.That(runner.RunUntilUpdateCounter(3), Is.True,
                $"updateCounter 3 not reached after {ScriptRunner.MaxTicks} ticks (used {runner.TicksUsed})");

            // counter advanced through multiple independent passes
            var inv = runner.GetGInv();
            Assert.That(inv.updateCounter, Is.GreaterThanOrEqualTo(3));
            Assert.That((double)receiver.AmountOf(SteelPlate), Is.GreaterThan(0.0),
                "receiver should have received items by the third pass");
        }
    }
}
