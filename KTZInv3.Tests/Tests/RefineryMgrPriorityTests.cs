using System;
using System.Collections.Generic;
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
    /// RefineryMgr ore priority derived from the assembler queues. Only the
    /// LEADING (first) queue item of each ASSEMBLY-mode assembler counts
    /// (an assembler cannot start subsequent items until the head
    /// completes), only items with a KNOWN composition (AsmLearn, learned
    /// by disassembly) contribute, only ingot ingredients matter (refineries
    /// make ingots, not components), and the ingot demand is mapped to ore
    /// demand through the learned refinery recipes (RefLearn). When there is
    /// queue demand it LEADS the ordering; the static orePriorityOrder
    /// follows as fallback for ores with no current demand.
    /// </summary>
    [TestFixture]
    public class RefineryMgrPriorityTests
    {
        static readonly MyItemType IronOre = MyItemType.MakeOre("Iron");
        static readonly MyItemType GoldOre = MyItemType.MakeOre("Gold");
        static readonly MyItemType NickelOre = MyItemType.MakeOre("Nickel");
        static readonly MyItemType StoneOre = MyItemType.MakeOre("Stone");
        static readonly MyItemType IronIngot = MyItemType.MakeIngot("Iron");
        static readonly MyItemType GoldIngot = MyItemType.MakeIngot("Gold");
        static readonly MyItemType NickelIngot = MyItemType.MakeIngot("Nickel");
        static readonly MyItemType SteelPlate = MyItemType.MakeComponent("SteelPlate");
        static readonly MyItemType Motor = MyItemType.MakeComponent("Motor");
        static readonly MyItemType ConstructionComponent = MyItemType.MakeComponent("ConstructionComponent");

        static readonly MyDefinitionId SteelPlateBp = new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "SteelPlate");
        static readonly MyDefinitionId MotorBp = new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "Motor");
        static readonly MyDefinitionId LargeRefineryDef = new MyDefinitionId(typeof(MyObjectBuilder_Refinery), "LargeRefinery");

        static readonly BindingFlags NF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            // extra ore/ingot types beyond the built-in set
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Gold", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Nickel", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Gold", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Nickel", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            IngameScript.Program.gProgram = Gateway.CreateProgram().Build();
            ResetStatics();
        }

        static void ResetStatics()
        {
            var pType = typeof(IngameScript.Program);
            pType.GetField("assemblers", NF).SetValue(null, new List<IMyAssembler>());
            pType.GetField("refineries", NF).SetValue(null, new List<IMyRefinery>());
            pType.GetNestedType("Autocraft", BindingFlags.NonPublic).GetField("blueprints", NF)
                .SetValue(null, new Dictionary<MyDefinitionId, MyDefinitionId>());
            pType.GetNestedType("AsmLearn", BindingFlags.NonPublic).GetField("known", NF)
                .SetValue(null, new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>());
            pType.GetNestedType("RefLearn", BindingFlags.NonPublic).GetField("learned", NF)
                .SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>());
            IngameScript.Program.Inventory.globalManifest.stuff.Clear();
            IngameScript.Program.tick = 0;
        }

        static void SetAssemblers(List<IMyAssembler> list)
            => typeof(IngameScript.Program).GetField("assemblers", NF).SetValue(null, list);

        static void SeedBlueprint(MyItemType item, MyDefinitionId bp)
        {
            var blueprints = (Dictionary<MyDefinitionId, MyDefinitionId>)typeof(IngameScript.Program)
                .GetNestedType("Autocraft", BindingFlags.NonPublic).GetField("blueprints", NF).GetValue(null);
            blueprints[(MyDefinitionId)item] = bp;
        }

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
                byOre[ore] = new Dictionary<MyItemType, MyFixedPoint> { [ingot] = (MyFixedPoint)ratio };
            learned[LargeRefineryDef] = byOre;
        }

        /// <summary>An assembler fake whose queue is a real list the test mutates.</summary>
        static (IMyAssembler asm, List<MyProductionItem> queue) MakeAssembler(MyAssemblerMode mode)
        {
            var queue = new List<MyProductionItem>();
            var asm = A.Fake<IMyAssembler>();
            A.CallTo(() => asm.Mode).Returns(mode);
            A.CallTo(() => asm.GetQueue(A<List<MyProductionItem>>.Ignored))
                .Invokes((List<MyProductionItem> q) => { q.Clear(); q.AddRange(queue); });
            return (asm, queue);
        }

        static List<MyItemType> ComputeQueueOrePriority()
            => (List<MyItemType>)typeof(IngameScript.Program).GetNestedType("RefineryMgr", BindingFlags.NonPublic)
                .GetMethod("computeQueueOrePriority", NF | BindingFlags.Public).Invoke(null, null);

        static object MakeMgr()
            => Activator.CreateInstance(typeof(IngameScript.Program).GetNestedType("RefineryMgr", BindingFlags.NonPublic), nonPublic: true);

        static void ComputeFactors(object mgr)
            => typeof(IngameScript.Program).GetNestedType("RefineryMgr", BindingFlags.NonPublic)
                .GetMethod("computeFactors", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Invoke(mgr, null);

        static List<MyItemType> AvailOrePriority(object mgr)
            => (List<MyItemType>)typeof(IngameScript.Program).GetNestedType("RefineryMgr", BindingFlags.NonPublic)
                .GetField("availOrePriority", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(mgr);

        // ---- queue demand drives the priority ----

        [Test]
        public void QueueDemand_LeadsPriorityOrder()
        {
            // 10 SteelPlate queued: 7 Iron + 1 Gold per unit -> iron demand
            // 70 (70/0.7 = 100 ore), gold 10 (10/0.5 = 20 ore) -> iron first
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedComposition(SteelPlate, new Dictionary<MyItemType, MyFixedPoint> {
                { IronIngot, (MyFixedPoint)7 }, { GoldIngot, (MyFixedPoint)1 } });
            SeedRefineryRecipe(
                (IronOre, IronIngot, 0.7),
                (GoldOre, GoldIngot, 0.5));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority, Is.EqualTo(new List<MyItemType> { IronOre, GoldOre }),
                "iron demand (100 ore equivalent) must lead gold (20 ore equivalent)");
        }

        [Test]
        public void OnlyHead_Counts_NotSubsequentQueueItems()
        {
            // head: 10 SteelPlate (iron+gold); second item: 100000 Motor
            // (nickel-heavy). The Motor must NOT contribute: an assembler
            // cannot start it until the head completes.
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            queue.Add(new MyProductionItem(1, MotorBp, (MyFixedPoint)100000));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedBlueprint(Motor, MotorBp);
            SeedComposition(SteelPlate, new Dictionary<MyItemType, MyFixedPoint> {
                { IronIngot, (MyFixedPoint)7 }, { GoldIngot, (MyFixedPoint)1 } });
            SeedComposition(Motor, new Dictionary<MyItemType, MyFixedPoint> {
                { NickelIngot, (MyFixedPoint)10 } });
            SeedRefineryRecipe(
                (IronOre, IronIngot, 0.7),
                (GoldOre, GoldIngot, 0.5),
                (NickelOre, NickelIngot, 0.8));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority, Is.EqualTo(new List<MyItemType> { IronOre, GoldOre }),
                "only the leading stack counts: the 100000 Motor order must contribute nothing");
        }

        [Test]
        public void NonIngotIngredients_AreIgnored()
        {
            // composition includes a component ingredient; refineries make
            // ingots only, so it must not produce ore demand
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, MotorBp, (MyFixedPoint)5));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(Motor, MotorBp);
            SeedComposition(Motor, new Dictionary<MyItemType, MyFixedPoint> {
                { IronIngot, (MyFixedPoint)2 }, { ConstructionComponent, (MyFixedPoint)1 } });
            SeedRefineryRecipe((IronOre, IronIngot, 0.7));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority, Is.EqualTo(new List<MyItemType> { IronOre }),
                "the ConstructionComponent ingredient must be filtered out");
        }

        // ---- skips and fallback ----

        [Test]
        public void UnknownComposition_Skipped_FallsBackToStatic()
        {
            // blueprint mapping exists but AsmLearn doesn't know the item
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedRefineryRecipe((IronOre, IronIngot, 0.7));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority.Count, Is.EqualTo(0),
                "unknown composition must be skipped -> empty list -> static fallback");
        }

        [Test]
        public void UnknownBlueprint_Skipped()
        {
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, MotorBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            // no blueprint mapping for Motor

            var priority = ComputeQueueOrePriority();

            Assert.That(priority.Count, Is.EqualTo(0));
        }

        [Test]
        public void DisassemblyMode_Assemblers_Ignored()
        {
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Disassembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedComposition(SteelPlate, new Dictionary<MyItemType, MyFixedPoint> { { IronIngot, (MyFixedPoint)7 } });
            SeedRefineryRecipe((IronOre, IronIngot, 0.7));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority.Count, Is.EqualTo(0),
                "disassembly-mode assemblers must not contribute assembly demand");
        }

        [Test]
        public void EmptyQueue_FallsBackToStatic()
        {
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly); // queue stays empty
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedComposition(SteelPlate, new Dictionary<MyItemType, MyFixedPoint> { { IronIngot, (MyFixedPoint)7 } });
            SeedRefineryRecipe((IronOre, IronIngot, 0.7));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority.Count, Is.EqualTo(0), "no queue -> no demand -> static fallback");
        }

        [Test]
        public void MultipleAssemblers_DemandSums()
        {
            var (asm1, q1) = MakeAssembler(MyAssemblerMode.Assembly);
            q1.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            var (asm2, q2) = MakeAssembler(MyAssemblerMode.Assembly);
            q2.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm1, asm2 });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedComposition(SteelPlate, new Dictionary<MyItemType, MyFixedPoint> {
                { IronIngot, (MyFixedPoint)7 }, { GoldIngot, (MyFixedPoint)1 } });
            SeedRefineryRecipe(
                (IronOre, IronIngot, 0.7),
                (GoldOre, GoldIngot, 0.5));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority, Is.EqualTo(new List<MyItemType> { IronOre, GoldOre }),
                "20 SteelPlate across two assemblers: iron (140/0.7=200) still leads gold (20/0.5=40)");
        }

        // ---- integration through computeFactors ----

        [Test]
        public void ComputeFactors_QueueDemand_BeatsStaticOrder()
        {
            // static order starts with Stone; queue demand for iron must win
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedComposition(SteelPlate, new Dictionary<MyItemType, MyFixedPoint> { { IronIngot, (MyFixedPoint)7 } });
            SeedRefineryRecipe((IronOre, IronIngot, 0.7));

            IngameScript.Program.Inventory.globalManifest.stuff[StoneOre] = (MyFixedPoint)5000;
            IngameScript.Program.Inventory.globalManifest.stuff[IronOre] = (MyFixedPoint)5000;

            var mgr = MakeMgr();
            ComputeFactors(mgr);

            var avail = AvailOrePriority(mgr);
            Assert.That(avail.Count, Is.GreaterThan(0));
            Assert.That(avail[0], Is.EqualTo(IronOre),
                "queue demand for iron must beat the static Stone-first order");
        }

        [Test]
        public void ComputeFactors_NoQueue_StaticOrderLeads()
        {
            SetAssemblers(new List<IMyAssembler>());
            IngameScript.Program.Inventory.globalManifest.stuff[StoneOre] = (MyFixedPoint)5000;
            IngameScript.Program.Inventory.globalManifest.stuff[IronOre] = (MyFixedPoint)5000;

            var mgr = MakeMgr();
            ComputeFactors(mgr);

            var avail = AvailOrePriority(mgr);
            Assert.That(avail.Count, Is.GreaterThan(0));
            Assert.That(avail[0], Is.EqualTo(StoneOre),
                "no queue demand -> static orePriorityOrder leads (Stone first)");
        }
    }
}
