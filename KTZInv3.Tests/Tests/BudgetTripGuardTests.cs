using System;
using FakeItEasy;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// The budget trip guard: Runtime.MaxInstructionCount is 50000 / MaxCallChainDepth
    /// 1000 in the game; we trip at 90% with a script-defined exception that Main
    /// catches silently, so a runaway tick aborts cleanly instead of the engine
    /// terminating the script (ScriptOutOfInstructionsException etc.).
    /// </summary>
    [TestFixture]
    public class BudgetTripGuardTests
    {
        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ScriptRunner.ResetStatics();
        }

        static IMyGridProgramRuntimeInfo TripRuntime(int maxInstructions, int currentInstructions)
        {
            var fake = A.Fake<IMyGridProgramRuntimeInfo>();
            A.CallTo(() => fake.MaxInstructionCount).Returns(maxInstructions);
            A.CallTo(() => fake.MaxCallChainDepth).Returns(1000);
            A.CallTo(() => fake.CurrentInstructionCount).Returns(currentInstructions);
            A.CallTo(() => fake.CurrentCallChainDepth).Returns(0);
            return fake;
        }

        [Test]
        public void Ctor_ReadsBudgetsAt90Percent()
        {
            // game: 50000 instr / 1000 depth -> guard trips at 45000 / 900
            var runtime = TripRuntime(50000, 0);
            var program = Gateway.CreateProgram().WithRuntime(runtime).Build();

            Assert.That(IngameScript.Program.MaxInstructionCount, Is.EqualTo(45000),
                "MaxInstructionCount must be 90% of Runtime.MaxInstructionCount");
            Assert.That(IngameScript.Program.MaxCallChainDepth, Is.EqualTo(900),
                "MaxCallChainDepth must be 90% of Runtime.MaxCallChainDepth");
        }

        [Test]
        public void UnderBudget_NeverTrips()
        {
            // CurrentInstructionCount stays well under the 90% threshold the whole
            // run: the script must behave exactly as before (no trip, work happens).
            var grid = CargoFactory.CreateGrid();
            var source = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)5.0, grid,
                (new MyItemType("MyObjectBuilder_Component", "SteelPlate"), (MyFixedPoint)500));
            var receiver = CargoFactory.CreateCargo("1 Cargo [Components].P99", (MyFixedPoint)5.0, grid);

            var gts = new FakeGts();
            gts.Blocks.Add(source.Block);
            gts.Blocks.Add(receiver.Block);
            var runtime = TripRuntime(50000, 0); // Current always 0
            var runner = ScriptRunner.Create(gts, MeFactory.CreateMe(grid), runtime);

            Assert.That(runner.RunUntilUpdateCounter(2), Is.True,
                $"normal run must not trip; used {runner.TicksUsed} ticks");
            Assert.That((double)receiver.AmountOf(new MyItemType("MyObjectBuilder_Component", "SteelPlate")),
                Is.GreaterThan(0.0), "items must have moved");
        }

        [Test]
        public void OverBudget_TripsAndMainCatchesSilently()
        {
            // CurrentInstructionCount is pinned above the 90% threshold: every
            // guard site fires, TripExecution throws, Main catches -> the script
            // survives (no exception escapes) but does no inventory work.
            var grid = CargoFactory.CreateGrid();
            var source = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)5.0, grid,
                (new MyItemType("MyObjectBuilder_Component", "SteelPlate"), (MyFixedPoint)500));
            var receiver = CargoFactory.CreateCargo("1 Cargo [Components].P99", (MyFixedPoint)5.0, grid);

            var gts = new FakeGts();
            gts.Blocks.Add(source.Block);
            gts.Blocks.Add(receiver.Block);
            var runtime = TripRuntime(50000, 60000); // Current 60000 > 45000 threshold
            var runner = ScriptRunner.Create(gts, MeFactory.CreateMe(grid), runtime);
            runner.Build();

            // the script must NOT crash - Main catches ExecutionTripException
            // every tick. The trip fires at Main entry (before the tick counter
            // advances), so _ticks stays 0 and no inventory pass ever starts.
            for (int i = 0; i < 50; i++)
                runner.Program.Main("", UpdateType.Update1);

            var inv = runner.GetGInv();
            Assert.That(inv?.updateCounter ?? 0, Is.EqualTo(0),
                "no inventory pass may complete while over budget");
            Assert.That(IngameScript.Program._ticks, Is.EqualTo(0),
                "trip at Main entry must abandon the tick before _ticks advances");
            Assert.That(runner.Program.Main, Is.Not.Null, "script must still be alive after 50 tripping ticks");
        }

        [Test]
        public void OverDepth_TripsAtFunctionEntries()
        {
            // CurrentCallChainDepth pinned above the 90% threshold (900): the
            // entry-point guards fire, Main catches, script survives.
            var fake = A.Fake<IMyGridProgramRuntimeInfo>();
            A.CallTo(() => fake.MaxInstructionCount).Returns(50000);
            A.CallTo(() => fake.MaxCallChainDepth).Returns(1000);
            A.CallTo(() => fake.CurrentInstructionCount).Returns(0);
            A.CallTo(() => fake.CurrentCallChainDepth).Returns(950); // > 900 threshold

            var runner = ScriptRunner.Create(new FakeGts(), MeFactory.CreateMe(CargoFactory.CreateGrid()), fake);
            runner.Build();
            for (int i = 0; i < 30; i++)
                runner.Program.Main("", UpdateType.Update1);

            Assert.That(runner.Program.Main, Is.Not.Null,
                "depth trip must also be caught silently in Main");
        }

        [Test]
        public void Recovers_WhenBudgetFreesUp()
        {
            // the trip is per-tick: once the budget is healthy again the script
            // resumes real work. Fake reports over-budget for the first 10 ticks
            // (Current counts up), then drops to 0.
            int current = 60000;
            var fake = A.Fake<IMyGridProgramRuntimeInfo>();
            A.CallTo(() => fake.MaxInstructionCount).Returns(50000);
            A.CallTo(() => fake.MaxCallChainDepth).Returns(1000);
            A.CallTo(() => fake.CurrentInstructionCount).ReturnsLazily(() => current);
            A.CallTo(() => fake.CurrentCallChainDepth).Returns(0);

            var grid = CargoFactory.CreateGrid();
            var source = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)5.0, grid,
                (new MyItemType("MyObjectBuilder_Component", "SteelPlate"), (MyFixedPoint)500));
            var receiver = CargoFactory.CreateCargo("1 Cargo [Components].P99", (MyFixedPoint)5.0, grid);
            var gts = new FakeGts();
            gts.Blocks.Add(source.Block);
            gts.Blocks.Add(receiver.Block);

            var runner = ScriptRunner.Create(gts, MeFactory.CreateMe(grid), fake);
            runner.Build();

            // first 10 ticks: over budget -> tripped, no work
            for (int i = 0; i < 10; i++)
                runner.Program.Main("", UpdateType.Update1);
            Assert.That(runner.GetGInv()?.updateCounter ?? 0, Is.EqualTo(0),
                "no work during the over-budget phase");

            // budget frees up -> the very next tick must do real work
            current = 0;
            Assert.That(runner.RunUntilUpdateCounter(2), Is.True,
                "after the budget frees, inventory passes must resume");
            Assert.That((double)receiver.AmountOf(new MyItemType("MyObjectBuilder_Component", "SteelPlate")),
                Is.GreaterThan(0.0), "items must have moved after recovery");
        }
    }
}
