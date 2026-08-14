using System;
using System.Collections.Generic;
using FakeItEasy;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;
using VRage.ObjectBuilders;
using Sandbox.Common.ObjectBuilders;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// Exercises BPLearn2's blueprint->item association. The attribution rule
    /// mirrors RefLearn: only UNAMBIGUOUS observation windows are learned
    /// from. The assembler can complete several queue items within one
    /// 1-second observation (the game's production loop advances past items
    /// and produces whatever it can), so when MULTIPLE queue items changed or
    /// MULTIPLE output types increased, the pairing would be arbitrary - the
    /// window is skipped instead of learning a wrong association. These tests
    /// pin that behavior, especially the fast-crafting mislearn it prevents.
    /// </summary>
    [TestFixture]
    public class BPLearn2Tests
    {
        static readonly MyDefinitionId SteelPlateBp = new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "SteelPlate");
        static readonly MyDefinitionId InteriorPlateBp = new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "InteriorPlate");
        static readonly MyItemType SteelPlate = MyItemType.MakeComponent("SteelPlate");
        static readonly MyItemType InteriorPlate = MyItemType.MakeComponent("InteriorPlate");

        IngameScript.Program _program;

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ItemDefinitions.RegisterItem("MyObjectBuilder_Component", "SteelPlate", 0.0003f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Component", "InteriorPlate", 0.0003f, 1.0f, (MyFixedPoint)1000000);
            ResetStatics();

            _program = Gateway.CreateProgram().Build();
            IngameScript.Program.gProgram = _program;
            IngameScript.Program.tick = 0;
        }

        static void ResetStatics()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            // clear the blueprint registry (Autocraft.blueprints)
            var autocraftType = typeof(IngameScript.Program).GetNestedType("Autocraft", System.Reflection.BindingFlags.NonPublic);
            autocraftType.GetField("blueprints", flags).SetValue(null, new Dictionary<MyDefinitionId, MyDefinitionId>());
        }

        /// <summary>
        /// An assembler fake whose queue and output are real lists, so tests
        /// simulate production by mutating them between observations.
        /// </summary>
        static (IMyAssembler asm, List<MyProductionItem> queue, FakeInventory output) MakeAssembler()
        {
            var queue = new List<MyProductionItem>();
            var output = new FakeInventory((MyFixedPoint)5.0);
            var asm = A.Fake<IMyAssembler>();
            A.CallTo(() => asm.Mode).Returns(MyAssemblerMode.Assembly);
            A.CallTo(() => asm.CurrentProgress).Returns(0.5f);
            A.CallTo(() => asm.OutputInventory).Returns(output);
            A.CallTo(() => asm.GetQueue(A<List<MyProductionItem>>.Ignored))
                .Invokes((List<MyProductionItem> q) => { q.Clear(); q.AddRange(queue); });
            return (asm, queue, output);
        }

        static object MakeLearner(IMyAssembler asm)
        {
            var learner = Activator.CreateInstance(typeof(IngameScript.Program).GetNestedType("BPLearn2", System.Reflection.BindingFlags.NonPublic), nonPublic: true);
            var asmField = learner.GetType().GetField("asm", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            asmField.SetValue(learner, asm);
            return learner;
        }

        static void Update(object learner)
            => learner.GetType().GetMethod("update").Invoke(learner, null);

        static Dictionary<MyDefinitionId, MyDefinitionId> Blueprints()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            return (Dictionary<MyDefinitionId, MyDefinitionId>)typeof(IngameScript.Program)
                .GetNestedType("Autocraft", System.Reflection.BindingFlags.NonPublic).GetField("blueprints", flags).GetValue(null);
        }

        [Test]
        public void SingleCompletion_LearnsAssociation()
        {
            var (asm, queue, output) = MakeAssembler();
            var learner = MakeLearner(asm);

            // baseline: one SteelPlate order queued, output empty
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            Update(learner);

            // one unit produced: queue 10->9, output gained 1 steel plate
            queue[0] = new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)9);
            output.AddItem(SteelPlate, (MyFixedPoint)1);
            Update(learner);

            var bps = Blueprints();
            Assert.That(bps.ContainsKey((MyDefinitionId)SteelPlate), Is.True,
                "single unambiguous completion must learn the blueprint");
            Assert.That(bps[(MyDefinitionId)SteelPlate], Is.EqualTo(SteelPlateBp));
        }

        [Test]
        public void VanishedQueueItem_LearnsAssociation()
        {
            var (asm, queue, output) = MakeAssembler();
            var learner = MakeLearner(asm);

            queue.Add(new MyProductionItem(0, InteriorPlateBp, (MyFixedPoint)1));
            Update(learner);

            // the whole order completed: queue empty, output gained the item
            queue.Clear();
            output.AddItem(InteriorPlate, (MyFixedPoint)1);
            Update(learner);

            var bps = Blueprints();
            Assert.That(bps.ContainsKey((MyDefinitionId)InteriorPlate), Is.True);
            Assert.That(bps[(MyDefinitionId)InteriorPlate], Is.EqualTo(InteriorPlateBp));
        }

        [Test]
        public void FastCrafting_MultipleQueueChanges_SkipsWindow()
        {
            var (asm, queue, output) = MakeAssembler();
            var learner = MakeLearner(asm);

            // two different orders queued
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            queue.Add(new MyProductionItem(1, InteriorPlateBp, (MyFixedPoint)10));
            Update(learner);

            // fast crafting: BOTH decreased in one second, output gained both
            queue[0] = new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)8);
            queue[1] = new MyProductionItem(1, InteriorPlateBp, (MyFixedPoint)7);
            output.AddItem(SteelPlate, (MyFixedPoint)2);
            output.AddItem(InteriorPlate, (MyFixedPoint)3);
            Update(learner);

            Assert.That(Blueprints().Count, Is.EqualTo(0),
                "ambiguous window (multiple queue changes) must not learn anything");
        }

        [Test]
        public void FastCrafting_MultipleOutputIncreases_SkipsWindow()
        {
            var (asm, queue, output) = MakeAssembler();
            var learner = MakeLearner(asm);

            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            Update(learner);

            // one queue item changed, but the output gained TWO types
            // (e.g. the sorter pushed something in, or leftovers): ambiguous
            queue[0] = new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)9);
            output.AddItem(SteelPlate, (MyFixedPoint)1);
            output.AddItem(InteriorPlate, (MyFixedPoint)1);
            Update(learner);

            Assert.That(Blueprints().Count, Is.EqualTo(0),
                "multiple output increases in one window must not be attributed");
        }

        [Test]
        public void QueueChange_WithoutOutputIncrease_SkipsWindow()
        {
            var (asm, queue, output) = MakeAssembler();
            var learner = MakeLearner(asm);

            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            Update(learner);

            // the queue changed but nothing appeared in output (e.g. the
            // order was removed externally): no production happened
            queue[0] = new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)9);
            Update(learner);

            Assert.That(Blueprints().Count, Is.EqualTo(0));
        }

        [Test]
        public void OutputIncrease_WithoutQueueChange_SkipsWindow()
        {
            var (asm, queue, output) = MakeAssembler();
            var learner = MakeLearner(asm);

            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            Update(learner);

            // output gained something but the queue is untouched (sorter
            // pushed it in): must not be attributed to the queue head
            output.AddItem(SteelPlate, (MyFixedPoint)1);
            Update(learner);

            Assert.That(Blueprints().Count, Is.EqualTo(0));
        }

        [Test]
        public void AlreadyKnownBlueprint_NotRelearned()
        {
            var (asm, queue, output) = MakeAssembler();
            var learner = MakeLearner(asm);

            // the association is already in the registry
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var known = new Dictionary<MyDefinitionId, MyDefinitionId> { [(MyDefinitionId)SteelPlate] = SteelPlateBp };
            typeof(IngameScript.Program).GetNestedType("Autocraft", System.Reflection.BindingFlags.NonPublic)
                .GetField("blueprints", flags).SetValue(null, known);

            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            Update(learner);
            queue[0] = new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)9);
            output.AddItem(SteelPlate, (MyFixedPoint)1);
            Update(learner);

            // still exactly the one entry (no duplicate/relearn log)
            Assert.That(Blueprints().Count, Is.EqualTo(1));
        }
    }
}
