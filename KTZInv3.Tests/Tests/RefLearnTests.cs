using System;
using System.Collections.Generic;
using System.Linq;
using FakeItEasy;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// Exercises RefLearn, the observational refinery learner: it watches a
    /// refinery's input/output inventories and learns ore -> output conversions
    /// (with ratios) purely from inventory deltas. Covers single-output ores,
    /// multi-output ores (stone), ambiguous multi-input windows (skipped), and
    /// rolling ratio accumulation across partial windows.
    /// </summary>
    [TestFixture]
    public class RefLearnTests
    {
        static readonly MyItemType IronOre = new MyItemType("MyObjectBuilder_Ore", "Iron");
        static readonly MyItemType Stone = new MyItemType("MyObjectBuilder_Ore", "Stone");
        static readonly MyItemType GoldOre = new MyItemType("MyObjectBuilder_Ore", "Gold");
        static readonly MyItemType IronIngot = new MyItemType("MyObjectBuilder_Ingot", "Iron");
        static readonly MyItemType GoldIngot = new MyItemType("MyObjectBuilder_Ingot", "Gold");
        static readonly MyItemType Gravel = new MyItemType("MyObjectBuilder_Component", "Gravel");
        static readonly MyItemType Nickel = new MyItemType("MyObjectBuilder_Ingot", "Nickel");
        static readonly MyItemType Silicon = new MyItemType("MyObjectBuilder_Ingot", "Silicon");

        IngameScript.Program _program;

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Iron", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Stone", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Gold", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Iron", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Gold", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Nickel", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Silicon", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Component", "Gravel", 0.0001f, 1.0f, (MyFixedPoint)1000);

            ResetLearned();

            _program = Gateway.CreateProgram().Build();
            IngameScript.Program.gProgram = _program;
            IngameScript.Program.APIWC = new IngameScript.WcPbApi();
            IngameScript.Program.tick = 0;
        }

        static void ResetLearned()
        {
            var t = RefLearnType();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            t.GetField("learned", flags).SetValue(null, new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>());
            t.GetField("consumedTotal", flags).SetValue(null, new Dictionary<MyItemType, MyFixedPoint>());
            t.GetField("producedTotal", flags).SetValue(null, new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>());
        }

        static Type RefLearnType()
            => typeof(IngameScript.Program).GetNestedType("RefLearn", System.Reflection.BindingFlags.NonPublic);

        static Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> Learned()
            => (Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>)RefLearnType()
                .GetField("learned", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .GetValue(null);

        /// <summary>A refinery mock with real-behavior input/output FakeInventories.</summary>
        static (IMyRefinery refinery, FakeInventory input, FakeInventory output) MakeRefinery()
        {
            var input = new FakeInventory((MyFixedPoint)50.0);
            var output = new FakeInventory((MyFixedPoint)50.0);
            var refinery = A.Fake<IMyRefinery>();
            A.CallTo(() => refinery.InputInventory).Returns(input);
            A.CallTo(() => refinery.OutputInventory).Returns(output);
            return (refinery, input, output);
        }

        /// <summary>Advances tick by one second (RefLearn observes on tick%60==0).</summary>
        static void Tick()
        {
            IngameScript.Program.tick += 60;
        }

        /// <summary>A learner bound to one refinery; reuse across updates so
        /// its snapshot state (lastInput/lastOutput) persists between calls,
        /// exactly like RefineryMgr's per-refinery learners.</summary>
        class RefLearner
        {
            public object instance;
            public RefLearner(IMyRefinery refinery)
            {
                instance = Activator.CreateInstance(RefLearnType(), nonPublic: true);
                RefLearnType().GetField("machine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .SetValue(instance, refinery);
            }
            public void Update() => RefLearnType().GetMethod("update").Invoke(instance, null);
        }

        /// <summary>Creates a learner bound to the refinery and runs one update
        /// so the baseline snapshot is taken; later ticks then diff against it.</summary>
        static RefLearner RunUpdate(IMyRefinery refinery)
        {
            var learner = new RefLearner(refinery);
            learner.Update(); // baseline snapshot
            return learner;
        }

        [Test]
        public void SingleOutputOre_LearnsConversionAndRatio()
        {
            var (refinery, input, output) = MakeRefinery();
            input.AddItem(IronOre, (MyFixedPoint)100);

            Tick();
            var learner = RunUpdate(refinery); // baseline snapshot

            // refinery consumed 70 iron ore, produced 49 iron ingots
            input.Clear();
            input.AddItem(IronOre, (MyFixedPoint)30);
            output.AddItem(IronIngot, (MyFixedPoint)49);

            Tick();
            learner.Update();

            var learned = Learned();
            Assert.That(learned.ContainsKey(IronOre), Is.True, "iron ore conversion should be learned");
            Assert.That(learned[IronOre].ContainsKey(IronIngot), Is.True);
            Assert.That((double)learned[IronOre][IronIngot], Is.EqualTo(0.7).Within(0.001),
                "ratio should be produced/consumed = 49/70");
        }

        [Test]
        public void MultiOutputOre_LearnsAllOutputsWithOwnRatios()
        {
            var (refinery, input, output) = MakeRefinery();
            input.AddItem(Stone, (MyFixedPoint)100);

            Tick();
            var learner = RunUpdate(refinery); // baseline

            // stone -> gravel + iron + nickel + silicon, each with its own ratio
            input.Clear();
            output.AddItem(Gravel, (MyFixedPoint)80);
            output.AddItem(IronIngot, (MyFixedPoint)2);
            output.AddItem(Nickel, (MyFixedPoint)1);
            output.AddItem(Silicon, (MyFixedPoint)3);

            Tick();
            learner.Update();

            var learned = Learned();
            Assert.That(learned.ContainsKey(Stone), Is.True, "stone conversion should be learned");
            Assert.That((double)learned[Stone][Gravel], Is.EqualTo(0.8).Within(0.001));
            Assert.That((double)learned[Stone][IronIngot], Is.EqualTo(0.02).Within(0.001));
            Assert.That((double)learned[Stone][Nickel], Is.EqualTo(0.01).Within(0.001));
            Assert.That((double)learned[Stone][Silicon], Is.EqualTo(0.03).Within(0.001));
        }

        [Test]
        public void AmbiguousWindow_MultipleInputsConsumed_IsSkipped()
        {
            var (refinery, input, output) = MakeRefinery();
            input.AddItem(IronOre, (MyFixedPoint)100);
            input.AddItem(GoldOre, (MyFixedPoint)100);

            Tick();
            var learner = RunUpdate(refinery); // baseline

            // BOTH ores consumed in one window: outputs can't be attributed to
            // a single input, so nothing is learned.
            input.Clear();
            input.AddItem(IronOre, (MyFixedPoint)50);
            input.AddItem(GoldOre, (MyFixedPoint)50);
            output.AddItem(IronIngot, (MyFixedPoint)35);
            output.AddItem(GoldIngot, (MyFixedPoint)35);

            Tick();
            learner.Update();

            Assert.That(Learned().Count, Is.Zero,
                "ambiguous multi-input window must not be learned from");
        }

        [Test]
        public void Ratio_AccumulatesAcrossPartialWindows()
        {
            var (refinery, input, output) = MakeRefinery();

            // window 1: baseline
            input.AddItem(IronOre, (MyFixedPoint)100);
            Tick();
            var learner = RunUpdate(refinery);

            // window 2: consumed 70, produced 49 (0.7)
            input.Clear();
            input.AddItem(IronOre, (MyFixedPoint)30);
            output.AddItem(IronIngot, (MyFixedPoint)49);
            Tick();
            learner.Update();

            // window 3: consumed 30 more, produced 21 more (total output 70)
            input.Clear();
            output.Clear();
            output.AddItem(IronIngot, (MyFixedPoint)70);
            Tick();
            learner.Update();

            var learned = Learned();
            Assert.That(learned.ContainsKey(IronOre), Is.True);
            Assert.That((double)learned[IronOre][IronIngot], Is.EqualTo(0.7).Within(0.001),
                "rolling ratio (70+49)/(100+30)... should converge to 0.7");
        }

        [Test]
        public void NoLearn_WhenOutputDidNotChange()
        {
            var (refinery, input, output) = MakeRefinery();
            input.AddItem(IronOre, (MyFixedPoint)100);

            Tick();
            var learner = RunUpdate(refinery); // baseline

            // input consumed but nothing produced yet (batch in progress): no learn
            input.Clear();
            input.AddItem(IronOre, (MyFixedPoint)90);
            Tick();
            learner.Update();

            Assert.That(Learned().Count, Is.Zero);
        }
    }
}
