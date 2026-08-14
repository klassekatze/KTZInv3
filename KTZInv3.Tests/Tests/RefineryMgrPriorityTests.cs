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
    /// RefineryMgr ore priority derived from the assembler queues. Each
    /// ASSEMBLY-mode assembler's queue is walked from the head: a stack
    /// whose ingot needs are already covered by stock gives the refineries
    /// "nothing to do", so it is skipped and the NEXT stack is considered.
    /// The first stack with a real gap contributes its per-ingot SHORTFALL
    /// (not the full need), and satisfied stacks reserve their full need
    /// against a working stock copy so two assemblers queueing the same
    /// item both count. Only items with a KNOWN composition (AsmLearn,
    /// learned by disassembly) contribute; only ingot ingredients matter
    /// (refineries make ingots, not components). Ingot shortfall maps to
    /// ore demand through the learned refinery recipes (RefLearn). When
    /// there is queue demand it LEADS the ordering; the static
    /// orePriorityOrder follows as fallback.
    /// </summary>
    [TestFixture]
    public class RefineryMgrPriorityTests
    {
        static readonly MyItemType IronOre = MyItemType.MakeOre("Iron");
        static readonly MyItemType GoldOre = MyItemType.MakeOre("Gold");
        static readonly MyItemType NickelOre = MyItemType.MakeOre("Nickel");
        static readonly MyItemType StoneOre = MyItemType.MakeOre("Stone");
        static readonly MyItemType SiliconOre = MyItemType.MakeOre("Silicon");
        static readonly MyItemType LeadOre = MyItemType.MakeOre("Lead");
        static readonly MyItemType CopperOre = MyItemType.MakeOre("Copper");
        static readonly MyItemType IronIngot = MyItemType.MakeIngot("Iron");
        static readonly MyItemType GoldIngot = MyItemType.MakeIngot("Gold");
        static readonly MyItemType NickelIngot = MyItemType.MakeIngot("Nickel");
        static readonly MyItemType SiliconIngot = MyItemType.MakeIngot("Silicon");
        static readonly MyItemType LeadIngot = MyItemType.MakeIngot("Lead");
        static readonly MyItemType CopperIngot = MyItemType.MakeIngot("Copper");
        static readonly MyItemType SteelPlate = MyItemType.MakeComponent("SteelPlate");
        static readonly MyItemType Motor = MyItemType.MakeComponent("Motor");
        static readonly MyItemType PowerCell = MyItemType.MakeComponent("PowerCell");
        static readonly MyItemType ConstructionComponent = MyItemType.MakeComponent("ConstructionComponent");

        static readonly MyDefinitionId SteelPlateBp = new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "SteelPlate");
        static readonly MyDefinitionId MotorBp = new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "Motor");
        static readonly MyDefinitionId PowerCellBp = new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "PowerCell");
        static readonly MyDefinitionId LargeRefineryDef = new MyDefinitionId(typeof(MyObjectBuilder_Refinery), "LargeRefinery");

        static readonly BindingFlags NF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            // extra ore/ingot types beyond the built-in set
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Gold", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Nickel", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Silicon", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Lead", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Copper", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Stone", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Gold", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Nickel", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Silicon", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Lead", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Copper", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Iron", 0.00027f, 1.0f, (MyFixedPoint)1000000);
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

        static void SetIngotStock(params (MyItemType ingot, double amount)[] stock)
        {
            foreach (var (ingot, amount) in stock)
                IngameScript.Program.Inventory.globalManifest.stuff[ingot] = (MyFixedPoint)amount;
        }

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

        static bool AssemblerQueuesAllUnknown()
            => (bool)typeof(IngameScript.Program).GetNestedType("RefineryMgr", BindingFlags.NonPublic)
                .GetMethod("assemblerQueuesAllUnknown", NF | BindingFlags.Public).Invoke(null, null);

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

        // ---- queue walking: satisfied heads give refineries nothing to do ----

        [Test]
        public void SatisfiedHead_UsesNextQueueItem()
        {
            // head: 10 SteelPlate (70 iron + 10 gold) - fully in stock, so
            // the refineries have nothing to do for it; the NEXT stack
            // (Motor, nickel) must drive the priority instead
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            queue.Add(new MyProductionItem(1, MotorBp, (MyFixedPoint)5));
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
            SetIngotStock((IronIngot, 70), (GoldIngot, 10));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority, Is.EqualTo(new List<MyItemType> { NickelOre }),
                "satisfied head (SteelPlate) must be skipped; Motor's nickel demand leads");
        }

        [Test]
        public void SatisfiedHead_SubtractsItsIngots_BeforeNextGap()
        {
            // head: 10 SteelPlate needs 70 iron + 10 gold; stock has only 50
            // iron + 10 gold -> the head itself is UNSATISFIED (iron gap 20)
            // but the satisfied gold portion must be subtracted from the
            // working stock before the next stack's gap is computed. With
            // only one stack the demand is the head's own shortfall.
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedComposition(SteelPlate, new Dictionary<MyItemType, MyFixedPoint> {
                { IronIngot, (MyFixedPoint)7 }, { GoldIngot, (MyFixedPoint)1 } });
            SeedRefineryRecipe(
                (IronOre, IronIngot, 0.7),
                (GoldOre, GoldIngot, 0.5));
            SetIngotStock((IronIngot, 50), (GoldIngot, 10));

            var priority = ComputeQueueOrePriority();

            // iron gap 20 (70-50), gold gap 0 (10-10) -> only iron demand
            Assert.That(priority, Is.EqualTo(new List<MyItemType> { IronOre }));
        }

        [Test]
        public void TwoAssemblers_ReserveSatisfiedStock_BeforeSecondGap()
        {
            // two assemblers both queueing 10 SteelPlate (7 iron + 1 gold
            // each = 140 iron + 20 gold total). Stock: 100 iron + 0 gold.
            // With reservation the first assembler's satisfied iron need
            // (70) is subtracted before the second assembler's gap is
            // computed: iron gap = 140-100 = 40, gold gap = 20. WITHOUT
            // reservation both assemblers would see 100 iron >= 70 and be
            // "satisfied", leaving only gold demand. The ORDER is by
            // coverage (stock / per-unit need) ascending - the binding
            // constraint first: gold has ZERO stock (coverage 0) so it is
            // the bottleneck and its ore leads; iron stock covers
            // 100/7 = 14.3 units.
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
            SetIngotStock((IronIngot, 100));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority, Is.EqualTo(new List<MyItemType> { GoldOre, IronOre }),
                "gold has zero stock (coverage 0) so it is the binding constraint and must lead; reservation still makes iron gap 40");
        }

        [Test]
        public void AllSatisfied_FallsBackToStatic()
        {
            // every queued stack's ingots are in stock -> no refinery work
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedComposition(SteelPlate, new Dictionary<MyItemType, MyFixedPoint> {
                { IronIngot, (MyFixedPoint)7 }, { GoldIngot, (MyFixedPoint)1 } });
            SeedRefineryRecipe(
                (IronOre, IronIngot, 0.7),
                (GoldOre, GoldIngot, 0.5));
            SetIngotStock((IronIngot, 100), (GoldIngot, 20));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority.Count, Is.EqualTo(0),
                "all queued stacks satisfied -> no demand -> static fallback");
        }

        // ---- coverage ordering: the binding constraint leads ----

        [Test]
        public void BindingConstraint_Leads_NotLargestShortfall()
        {
            // The real-world PowerCell case: 999 cells (7 iron, 0.7
            // silicon, 1 nickel, 0.7 lead, 3 copper per unit). Stock covers
            // ~199 cells of iron, 65 of silicon, 26 of nickel, 50 of
            // copper, but only 0.14 cells of LEAD (0.0995 kg vs 0.7
            // needed). Lead is the binding constraint: the assembler cannot
            // make even ONE more cell without it, so refining copper (50
            // cells already in stock) unblocks nothing. Lead ore must be
            // first even though copper's absolute shortfall (2844) is much
            // larger than lead's (699).
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, PowerCellBp, (MyFixedPoint)999));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(PowerCell, PowerCellBp);
            SeedComposition(PowerCell, new Dictionary<MyItemType, MyFixedPoint> {
                { IronIngot, (MyFixedPoint)7 }, { SiliconIngot, (MyFixedPoint)0.7m },
                { NickelIngot, (MyFixedPoint)1 }, { LeadIngot, (MyFixedPoint)0.7m },
                { CopperIngot, (MyFixedPoint)3 } });
            SeedRefineryRecipe(
                (IronOre, IronIngot, 0.7),
                (SiliconOre, SiliconIngot, 0.7),
                (NickelOre, NickelIngot, 0.4),
                (LeadOre, LeadIngot, 0.16),
                (CopperOre, CopperIngot, 0.24));
            SetIngotStock((IronIngot, 1393), (SiliconIngot, 45), (NickelIngot, 26), (LeadIngot, 0.0995), (CopperIngot, 152));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority[0], Is.EqualTo(LeadOre),
                "lead covers 0.14 cells - the binding constraint - so lead ore leads regardless of copper's larger absolute shortfall");
            Assert.That(priority.IndexOf(CopperOre), Is.GreaterThan(priority.IndexOf(LeadOre)),
                "copper (50 cells covered) must rank after lead");
        }

        [Test]
        public void InefficientSources_DoNotInflateDemand()
        {
            // stone produces iron at a terrible ratio (0.03/stone). The
            // old code divided the iron shortfall by EVERY ore's ratio, so
            // stone's 5599/0.03 = 186k "demand" dominated the list. Demand
            // must be attributed to the most efficient source (iron ore,
            // 0.7) so stone never outranks it.
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);
            SeedComposition(SteelPlate, new Dictionary<MyItemType, MyFixedPoint> { { IronIngot, (MyFixedPoint)7 } });
            SeedRefineryRecipe(
                (IronOre, IronIngot, 0.7),
                (StoneOre, IronIngot, 0.03));

            var priority = ComputeQueueOrePriority();

            Assert.That(priority, Is.EqualTo(new List<MyItemType> { IronOre }),
                "iron shortfall must be attributed to the efficient source (iron ore), not inflated via stone's 0.03 ratio");
        }

        // ---- status display helpers ----

        [Test]
        public void AssemblerQueuesAllUnknown_True_WhenNoBlueprintKnown()
        {
            // queue exists (SteelPlate) but the blueprint mapping is empty
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            // no SeedBlueprint call -> unknown

            Assert.That(AssemblerQueuesAllUnknown(), Is.True,
                "queues exist but no blueprint is known -> flag the status annotation");
        }

        [Test]
        public void AssemblerQueuesAllUnknown_False_WhenKnown()
        {
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly);
            queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            SetAssemblers(new List<IMyAssembler> { asm });
            SeedBlueprint(SteelPlate, SteelPlateBp);

            Assert.That(AssemblerQueuesAllUnknown(), Is.False, "a known queued blueprint -> no annotation");
        }

        [Test]
        public void AssemblerQueuesAllUnknown_False_WhenNoQueues()
        {
            var (asm, queue) = MakeAssembler(MyAssemblerMode.Assembly); // empty queue
            SetAssemblers(new List<IMyAssembler> { asm });

            Assert.That(AssemblerQueuesAllUnknown(), Is.False, "no queues at all -> no annotation");
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
