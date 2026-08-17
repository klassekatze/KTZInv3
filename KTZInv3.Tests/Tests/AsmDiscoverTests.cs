using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Exercises AsmDiscover, the assembler recipe discovery controller: when
    /// we know an autocraft blueprint for an item and possess at least one
    /// copy of the item, an assembler is isolated (conveyors off, locked,
    /// flushed), one copy is stuffed into its input, and its disassembly is
    /// queued. The exact composition (output delta for one unit) is recorded
    /// in AsmLearn and written to CustomData as the KTZREC; section.
    /// Critically, the user's queue and assembler mode are restored on
    /// release — discovery must not destroy queued teaching jobs.
    /// </summary>
    [TestFixture]
    public class AsmDiscoverTests
    {
        static readonly MyItemType SteelPlate = MyItemType.MakeComponent("SteelPlate");
        static readonly MyItemType IronIngot = new MyItemType("MyObjectBuilder_Ingot", "Iron");
        static readonly MyItemType GoldIngot = new MyItemType("MyObjectBuilder_Ingot", "Gold");
        static readonly MyDefinitionId SteelPlateDef = new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlate");
        static readonly MyDefinitionId SteelPlateBp = new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlateBlueprint");
        static readonly MyDefinitionId InteriorPlateBp = new MyDefinitionId(typeof(MyObjectBuilder_Component), "InteriorPlateBlueprint");

        IngameScript.Program _program;

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ItemDefinitions.RegisterItem("MyObjectBuilder_Component", "SteelPlate", 0.0003f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Iron", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Gold", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ResetStatics();

            _program = Gateway.CreateProgram().Build();
            IngameScript.Program.gProgram = _program;
            IngameScript.Program.APIWC = new IngameScript.WcPbApi();
            IngameScript.Program.tick = 0;

            var pType = typeof(IngameScript.Program);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var gInvField = pType.GetField("gInv", flags);
            if (gInvField != null) gInvField.SetValue(null, new IngameScript.Program.Inventory());
            SetStatic(pType, flags, "gAssemblerMgr");
            SetStatic(pType, flags, "gRefineryMgr");
            SetStatic(pType, flags, "gAutocraft");
            SetStatic(pType, flags, "gReactorMgr");
            var assemblersField = pType.GetField("assemblers", flags);
            if (assemblersField != null)
                assemblersField.SetValue(null, new List<IMyAssembler>());
        }

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
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            // AsmLearn registry
            var asmLearnType = typeof(IngameScript.Program).GetNestedType("AsmLearn", System.Reflection.BindingFlags.NonPublic);
            asmLearnType.GetField("known", flags).SetValue(null, new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>());
            // AsmDiscover static state
            var adType = typeof(IngameScript.Program).GetNestedType("AsmDiscover", System.Reflection.BindingFlags.NonPublic);
            adType.GetField("discAssembler", flags).SetValue(null, null);
            adType.GetField("inBaseline", flags).SetValue(null, null);
            adType.GetField("discQueueBackup", flags).SetValue(null, null);
            adType.GetField("retrieveCooldown", flags).SetValue(null, new Dictionary<MyDefinitionId, int>());
            // RefLearn registry (same test process)
            var refLearnType = typeof(IngameScript.Program).GetNestedType("RefLearn", System.Reflection.BindingFlags.NonPublic);
            refLearnType.GetField("learned", flags).SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>());
            refLearnType.GetField("consumedTotal", flags).SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, MyFixedPoint>>());
            refLearnType.GetField("producedTotal", flags).SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>());

            IngameScript.Program.Inventory.globalManifest.stuff.Clear();
            IngameScript.Program.Inventory.globalManifest.maxVolume = 0;
            IngameScript.Program.Inventory.globalManifest.freeVolume = 0;
            IngameScript.Program.Inventory.globalManifest.typeVolume.Clear();
            IngameScript.Program.Inventory.BlockInventory.bPriorityList.Clear();
            IngameScript.Program.Inventory.BlockInventory.bIDict.Clear();
            IngameScript.Program.Inventory.BlockInventory.idl = 0;
        }

        /// <summary>
        /// Mutable state captured behind the fake's setters, so tests can
        /// assert what the script set (mode, UseConveyorSystem) and what the
        /// queue looks like after ClearQueue/AddQueueItem/GetQueue.
        /// Models the game's per-mode queues (decompiled MyAssembler:
        /// the DisassembleEnabled setter calls SwapQueue(ref m_otherQueue)):
        /// setting Mode swaps the visible queue with the stashed one, so a
        /// mode round-trip preserves each side exactly.
        /// </summary>
        sealed class AsmState
        {
            public MyAssemblerMode Mode = MyAssemblerMode.Assembly;
            public bool UseConv = true;
            public List<MyProductionItem> AssemblyQueue = new List<MyProductionItem>();
            public List<MyProductionItem> DisassemblyQueue = new List<MyProductionItem>();
            public List<MyProductionItem> Queue => Mode == MyAssemblerMode.Disassembly ? DisassemblyQueue : AssemblyQueue;
        }

        /// <summary>An enabled assembler fake with captured setter state and per-mode queues.</summary>
        static (IMyAssembler asm, FakeInventory input, FakeInventory output, AsmState state) MakeAssembler()
        {
            var input = new FakeInventory((MyFixedPoint)5.0);
            var output = new FakeInventory((MyFixedPoint)5.0);
            var state = new AsmState();

            var asm = A.Fake<IMyAssembler>();
            A.CallTo(() => asm.InputInventory).Returns(input);
            A.CallTo(() => asm.OutputInventory).Returns(output);
            A.CallTo(() => asm.BlockDefinition).Returns((SerializableDefinitionId)new MyDefinitionId(typeof(MyObjectBuilder_Assembler), "LargeAssembler"));
            A.CallTo(() => asm.Enabled).Returns(true);
            A.CallTo(() => asm.CustomName).Returns("Assembler");
            A.CallTo(() => asm.IsProducing).Returns(false);
            A.CallTo(() => asm.IsQueueEmpty).ReturnsLazily(() => state.Queue.Count == 0);
            A.CallTo(() => asm.CanUseBlueprint(A<MyDefinitionId>.Ignored)).Returns(true);
            A.CallTo(() => asm.Mode).ReturnsLazily(() => state.Mode);
            // setting Mode swaps the visible queue (per-mode queues), which
            // the derived AsmState.Queue property models automatically
            A.CallToSet(() => asm.Mode).Invokes((MyAssemblerMode m) => state.Mode = m);
            A.CallTo(() => asm.UseConveyorSystem).ReturnsLazily(() => state.UseConv);
            A.CallToSet(() => asm.UseConveyorSystem).Invokes((bool v) => state.UseConv = v);
            A.CallTo(() => asm.ClearQueue()).Invokes(() => state.Queue.Clear());
            A.CallTo(() => asm.AddQueueItem(A<MyDefinitionId>.Ignored, A<MyFixedPoint>.Ignored))
                .Invokes((MyDefinitionId bp, MyFixedPoint amt) => state.Queue.Add(new MyProductionItem(0, bp, amt)));
            A.CallTo(() => asm.GetQueue(A<List<MyProductionItem>>.Ignored))
                .Invokes((List<MyProductionItem> q) => { q.Clear(); q.AddRange(state.Queue); });
            // the inventory pipeline links blocks through InventoryCount + GetInventory(i)
            A.CallTo(() => asm.InventoryCount).Returns(2);
            A.CallTo(() => asm.GetInventory(0)).Returns(input);
            A.CallTo(() => asm.GetInventory(1)).Returns(output);
            return (asm, input, output, state);
        }

        /// <summary>Runs the inventory pipeline (SORT off — these tests exercise AsmDiscover, not the sorter).</summary>
        static void RunPipelineAndScan(List<IMyTerminalBlock> blocks, int ticks = 300)
        {
            var sortField = typeof(IngameScript.Program).GetField("SORT", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var prevSort = (bool)sortField.GetValue(null);
            sortField.SetValue(null, false);
            try
            {
                var inv = new IngameScript.Program.Inventory();
                var gInvField = typeof(IngameScript.Program).GetField("gInv", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                gInvField.SetValue(null, inv);
                inv.updateContainers(blocks);
                for (int i = 0; i < ticks; i++)
                {
                    IngameScript.Program.tick++;
                    inv.update();
                }
            }
            finally
            {
                sortField.SetValue(null, prevSort);
            }
        }

        /// <summary>Runs more pipeline ticks so updateM refreshes block manifests.</summary>
        static void RunTicks(int ticks)
        {
            var inv = (IngameScript.Program.Inventory)typeof(IngameScript.Program)
                .GetField("gInv", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            var sortField = typeof(IngameScript.Program).GetField("SORT", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var prevSort = (bool)sortField.GetValue(null);
            sortField.SetValue(null, false);
            try
            {
                for (int i = 0; i < ticks; i++)
                {
                    IngameScript.Program.tick++;
                    inv.update();
                }
            }
            finally
            {
                sortField.SetValue(null, prevSort);
            }
        }

        static void SetAssemblers(List<IMyAssembler> list)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            typeof(IngameScript.Program).GetField("assemblers", flags).SetValue(null, list);
        }

        static void SetBlueprints(Dictionary<MyDefinitionId, MyDefinitionId> dict)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            typeof(IngameScript.Program).GetNestedType("Autocraft", System.Reflection.BindingFlags.NonPublic)
                .GetField("blueprints", flags).SetValue(null, dict);
        }

        static object MakeDiscover()
            => Activator.CreateInstance(typeof(IngameScript.Program).GetNestedType("AsmDiscover", System.Reflection.BindingFlags.NonPublic), nonPublic: true);

        static void Update(object discover)
            => typeof(IngameScript.Program).GetNestedType("AsmDiscover", System.Reflection.BindingFlags.NonPublic)
                .GetMethod("update").Invoke(discover, null);

        static bool IsDiscovering(IMyAssembler a)
            => (bool)typeof(IngameScript.Program).GetNestedType("AsmDiscover", System.Reflection.BindingFlags.NonPublic)
                .GetMethod("isDiscovering", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { (IMyCubeBlock)a });

        [Test]
        public void UnknownComposition_WithBPAndItemCopy_StartsDiscovery()
        {
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)1));
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });
            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);

            Assert.That(IsDiscovering(asm), Is.True, "unknown composition with a BP and a copy must start discovery");
            // the item to disassemble lives in the OUTPUT inventory (the
            // game's UpdateDisassembleMode pulls it there); ingredients will
            // land in the INPUT inventory
            Assert.That((double)output.AmountOf(SteelPlate), Is.GreaterThan(0.0),
                "one copy of the item must be stuffed into the assembler OUTPUT inventory");
            Assert.That(state.UseConv, Is.False, "assembler's own conveyor auto-move must be disabled during discovery");
            Assert.That(state.Mode, Is.EqualTo(MyAssemblerMode.Disassembly), "assembler must be in disassembly mode during observation");
            Assert.That(state.Queue.Count, Is.EqualTo(1), "the disassembly job must be queued");
            Assert.That(state.Queue[0].BlueprintId, Is.EqualTo(SteelPlateBp));
            Assert.That((double)state.Queue[0].Amount, Is.EqualTo(1.0), "the queue amount is positive; the Mode drives the direction");
            // locked against the sorter
            var bi = IngameScript.Program.Inventory.BlockInventory.getBI(asm);
            Assert.That(bi.locked, Is.True, "discovering assembler must be locked to the sorter");
            // status display: learning line
            Assert.That(LearningStatus(), Is.EqualTo("Learning SteelPlate..."),
                "status display must show the discovery in progress");
        }

        static string LearningStatus()
            => (string)typeof(IngameScript.Program).GetNestedType("AsmDiscover", System.Reflection.BindingFlags.NonPublic)
                .GetMethod("learningStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Invoke(null, null);

        [Test]
        public void NoItemCopy_DoesNotStartDiscovery()
        {
            // cargo holds no steel plates at all
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0);
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });
            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);

            Assert.That(IsDiscovering(asm), Is.False, "no copy of the item -> no discovery");
            Assert.That(state.Queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void NoBlueprint_DoesNotStartDiscovery()
        {
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)5));
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId>()); // no BP known
            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);

            Assert.That(IsDiscovering(asm), Is.False, "no known blueprint -> no discovery");
            Assert.That(state.Queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void KnownComposition_DoesNotStartDiscovery()
        {
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)5));
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });
            // composition already known
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var known = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();
            known[SteelPlate] = new Dictionary<MyItemType, MyFixedPoint> { [IronIngot] = (MyFixedPoint)7, [GoldIngot] = (MyFixedPoint)1 };
            typeof(IngameScript.Program).GetNestedType("AsmLearn", System.Reflection.BindingFlags.NonPublic)
                .GetField("known", flags).SetValue(null, known);

            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);

            Assert.That(IsDiscovering(asm), Is.False, "known composition must not be re-discovered");
            Assert.That(state.Queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void DisassemblyCompletes_RecordsExactComposition()
        {
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)1));
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });
            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);
            Assert.That(IsDiscovering(asm), Is.True);

            // the disassembly completes: the game consumes the queue row,
            // deducts the item from the OUTPUT, and produces the exact
            // ingredients into the INPUT (steel plate = 7 iron + 1 gold)
            state.Queue.Clear();
            output.Clear();
            input.AddItem(IronIngot, (MyFixedPoint)7);
            input.AddItem(GoldIngot, (MyFixedPoint)1);
            // let the pipeline refresh the assembler's manifest (updateM)
            RunTicks(120);

            Update(discover);

            Assert.That(IsDiscovering(asm), Is.False, "completed disassembly must release the assembler");
            Assert.That(state.UseConv, Is.True, "UseConveyors must be restored on release");
            var bi = IngameScript.Program.Inventory.BlockInventory.getBI(asm);
            Assert.That(bi.locked, Is.False, "assembler must be unlocked on release");
            // exact composition recorded (per one disassembled unit)
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var known = (Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>)typeof(IngameScript.Program)
                .GetNestedType("AsmLearn", System.Reflection.BindingFlags.NonPublic).GetField("known", flags).GetValue(null);
            Assert.That(known.ContainsKey(SteelPlate), Is.True, "composition must be recorded");
            Assert.That((double)known[SteelPlate][IronIngot], Is.EqualTo(7.0));
            Assert.That((double)known[SteelPlate][GoldIngot], Is.EqualTo(1.0));
            // registry written to CustomData
            Assert.That(_program.Me.CustomData, Does.Contain("KTZREC;"), "release must write the composition registry to CustomData");
            Assert.That(_program.Me.CustomData, Does.Contain("MyObjectBuilder_Component/SteelPlate;MyObjectBuilder_Ingot/Iron;7"), "composition line must be in CustomData");
        }

        [Test]
        public void Release_RestoresClearedQueueAndMode()
        {
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)1));
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });

            // the user was teaching blueprints: 2 queued assembly jobs
            state.Queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)10));
            state.Queue.Add(new MyProductionItem(0, InteriorPlateBp, (MyFixedPoint)5));

            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);
            Assert.That(IsDiscovering(asm), Is.True, "discovery must start even with a busy queue");
            Assert.That(state.Queue.Count, Is.EqualTo(1), "the discovery run clears the queue and queues only the disassembly");
            Assert.That((double)state.Queue[0].Amount, Is.EqualTo(1.0));

            // the disassembly completes (queue row consumed + item
            // deducted + ingredients produced, all atomically by the game)
            state.Queue.Clear();
            output.Clear();
            input.AddItem(IronIngot, (MyFixedPoint)7);
            input.AddItem(GoldIngot, (MyFixedPoint)1);
            // let the pipeline refresh the assembler's manifest (updateM)
            RunTicks(120);
            Update(discover);

            Assert.That(IsDiscovering(asm), Is.False);
            // the user's jobs are back via the game's per-mode queue swap
            // (mode restored to Assembly re-materializes the stashed
            // assembly-side queue), plus the automatic re-queue of 1
            // replacement (the discovery consumed one copy of the item to
            // learn its composition). The manual re-add is GONE: stacking
            // the backup on top of the auto-restored stash doubled the
            // queue every cycle (the reported defect).
            Assert.That(state.Queue.Count, Is.EqualTo(3), "stashed jobs must re-materialize on mode restore + 1 replacement re-queued");
            Assert.That(state.Queue[0].BlueprintId, Is.EqualTo(SteelPlateBp));
            Assert.That((double)state.Queue[0].Amount, Is.EqualTo(10.0));
            Assert.That(state.Queue[1].BlueprintId, Is.EqualTo(InteriorPlateBp));
            Assert.That((double)state.Queue[1].Amount, Is.EqualTo(5.0));
            Assert.That(state.Queue[2].BlueprintId, Is.EqualTo(SteelPlateBp),
                "assembly of 1 replacement must be queued after a successful discovery");
            Assert.That((double)state.Queue[2].Amount, Is.EqualTo(1.0));
            Assert.That(state.Mode, Is.EqualTo(MyAssemblerMode.Assembly), "assembler mode must be restored");
        }

        [Test]
        public void Release_FailedRetrieval_DoesNotDoubleQueue_AndCoolsDown()
        {
            // Regression: the reported "endless doubling of craft orders".
            // A permanently unretrievable item (only copies counted by the
            // manifest sit inside the discovering assembler's own output,
            // which no sourcing view reaches) must (a) not corrupt the
            // queue on release and (b) not restart discovery every second.
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0);
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });

            // the user's assembly job, amount 100
            state.Queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)100));

            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            // phantom: the manifest says 1 steel plate exists, but no
            // physical copy is anywhere retrievable (simulate by putting
            // the manifest count without any physical items)
            IngameScript.Program.Inventory.globalManifest.stuff[SteelPlate] = (MyFixedPoint)1;

            var discover = MakeDiscover();
            Update(discover);
            Assert.That(IsDiscovering(asm), Is.False, "failed retrieval must release the assembler");
            Assert.That(state.Mode, Is.EqualTo(MyAssemblerMode.Assembly), "mode must be restored");
            Assert.That(state.Queue.Count, Is.EqualTo(1), "the user's queue must re-materialize exactly once (no doubling)");
            Assert.That((double)state.Queue[0].Amount, Is.EqualTo(100.0), "the re-materialized amount must be the original, not doubled");

            // cooldown: a second scan one second later must NOT restart
            Update(discover);
            IngameScript.Program.tick += 60;
            Update(discover);
            Assert.That(IsDiscovering(asm), Is.False, "cooldown must ban the unretrievable item for a while");
        }

        [Test]
        public void Start_CopiesAlreadyInOutput_CountAsFeedstock()
        {
            // the user's observation: in disassembly mode the OUTPUT is the
            // functional feed inventory. Copies already inside it must be
            // used as feedstock instead of triggering a doomed retrieval.
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0);
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });
            // 3 copies stranded in the assembler's OUTPUT (how the old
            // no-op output flush left SolarCells there)
            output.AddItem(SteelPlate, (MyFixedPoint)3);

            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);

            Assert.That(IsDiscovering(asm), Is.True, "in-place copies must satisfy the feedstock requirement");
            Assert.That(state.Queue.Count, Is.EqualTo(1), "disassembly queued");
            Assert.That((double)output.AmountOf(SteelPlate), Is.EqualTo(3.0), "in-place copies are kept, not flushed");
        }

        [Test]
        public void DisassemblyCompletes_WithRemainderInOutput_LearnsPerUnitRecipe()
        {
            // production regression (user-forced repro): >1 copies of the
            // item were already in the OUTPUT when discovery started. The
            // game disassembles exactly what the queue row says, deducts
            // items only at completion, and the remainder (5) stays in the
            // output. Completion is signaled by the queue row disappearing
            // (the game consumes it when the disassembly finishes), NOT by
            // the output being emptied - the OLD check (Amount > 0) could
            // never see "gone" and the run timed out, restarted, and
            // finally "learned" by summing every cell forced through in
            // one window (an N-times recipe).
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0);
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });
            // 6 copies pre-seated in the OUTPUT, exactly like the live grid
            output.AddItem(SteelPlate, (MyFixedPoint)6);

            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);
            Assert.That(IsDiscovering(asm), Is.True, "in-place copies satisfy feedstock, discovery starts");
            Assert.That((double)output.AmountOf(SteelPlate), Is.EqualTo(6.0), "copies kept as feedstock, not flushed");

            // the disassembly of 1x completes: the queue row is consumed,
            // ONE copy is deducted (6 -> 5), and the per-unit ingredients
            // appear in the input. The remainder stays in the output.
            state.Queue.Clear();
            output.RemoveItem(SteelPlate, (MyFixedPoint)1);
            input.AddItem(IronIngot, (MyFixedPoint)7);
            input.AddItem(GoldIngot, (MyFixedPoint)1);
            RunTicks(120);

            Update(discover);

            Assert.That(IsDiscovering(asm), Is.False, "must complete when the queue row is consumed despite 5 remaining");
            Assert.That(state.Mode, Is.EqualTo(MyAssemblerMode.Assembly), "mode must be restored");
            Assert.That(state.UseConv, Is.True, "UseConveyors must be restored");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var known = (Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>)typeof(IngameScript.Program)
                .GetNestedType("AsmLearn", System.Reflection.BindingFlags.NonPublic).GetField("known", flags).GetValue(null);
            Assert.That(known.ContainsKey(SteelPlate), Is.True, "composition must be recorded");
            Assert.That((double)known[SteelPlate][IronIngot], Is.EqualTo(7.0), "per-unit recipe, not 5x");
            Assert.That((double)known[SteelPlate][GoldIngot], Is.EqualTo(1.0), "per-unit recipe, not 5x");
            Assert.That(_program.Me.CustomData, Does.Contain("MyObjectBuilder_Component/SteelPlate;MyObjectBuilder_Ingot/Iron;7"), "per-unit line in registry");
        }

        [Test]
        public void DiscoveryReleases_WhenQueueRowConsumed_ButNoIngredients_NoTimeout()
        {
            // jam regression: the queue row disappears (job finished or
            // cancelled) but the input gained NOTHING (e.g. the disassembly
            // produced nothing usable, or the item was pulled). The old
            // code held the assembler until a 10-minute timeout; the new
            // rule releases IMMEDIATELY based on state: no queued blueprint
            // -> run is over, and no ingredients -> release without learn.
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0);
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });
            // feedstock present, so the run starts normally
            output.AddItem(SteelPlate, (MyFixedPoint)1);

            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);
            Assert.That(IsDiscovering(asm), Is.True, "run must start");

            // queue row vanishes (job done/cancelled) with NO ingredients
            state.Queue.Clear();
            // advance far beyond any imagined timeout - there is none
            IngameScript.Program.tick += 60 * 60 * 60;

            Update(discover);

            Assert.That(IsDiscovering(asm), Is.False, "must release immediately, no timeout");
            Assert.That(state.Mode, Is.EqualTo(MyAssemblerMode.Assembly), "mode must be restored");
            Assert.That(state.UseConv, Is.True, "UseConveyors must be restored");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var known = (Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>)typeof(IngameScript.Program)
                .GetNestedType("AsmLearn", System.Reflection.BindingFlags.NonPublic).GetField("known", flags).GetValue(null);
            Assert.That(known.ContainsKey(SteelPlate), Is.False, "nothing produced -> nothing learned");
        }

        [Test]
        public void DiscoveryReleases_WhenItemGoneFromOutput_NoIngredients()
        {
            // jam regression: the feedstock disappears from the OUTPUT
            // (pulled by something else) while the queue row still exists.
            // The run can never complete -> release immediately instead of
            // holding the assembler. No ingredients -> no learn.
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0);
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });
            output.AddItem(SteelPlate, (MyFixedPoint)1);
            // the user's assembly job, amount 100
            state.Queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)100));

            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);
            Assert.That(IsDiscovering(asm), Is.True, "run must start");

            // feedstock yanked from the output mid-run
            output.RemoveItem(SteelPlate, (MyFixedPoint)1);

            Update(discover);

            Assert.That(IsDiscovering(asm), Is.False, "item gone -> must release immediately");
            Assert.That(state.Mode, Is.EqualTo(MyAssemblerMode.Assembly), "mode must be restored");
            Assert.That(state.Queue.Count, Is.EqualTo(1), "stashed queue must re-materialize exactly once");
            Assert.That((double)state.Queue[0].Amount, Is.EqualTo(100.0), "no doubling on the jam-release path");
        }

        [Test]
        public void DisassemblyBackupMode_QueueReAddedManually()
        {
            // when the user's mode was Disassembly, the discovery's mode
            // flip never stashed anything (same mode), so release() must
            // re-add the cleared queue manually
            var cargo = CargoFactory.CreateCargo("2 Cargo [Components].P999", (MyFixedPoint)10.0, (SteelPlate, (MyFixedPoint)1));
            var (asm, input, output, state) = MakeAssembler();
            SetBlueprints(new Dictionary<MyDefinitionId, MyDefinitionId> { [SteelPlateDef] = SteelPlateBp });

            // user was in disassembly mode with a disassembly job queued
            state.Mode = MyAssemblerMode.Disassembly;
            state.Queue.Add(new MyProductionItem(0, SteelPlateBp, (MyFixedPoint)4));

            SetAssemblers(new List<IMyAssembler> { asm });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, asm });

            var discover = MakeDiscover();
            Update(discover);
            Assert.That(IsDiscovering(asm), Is.True);

            // disassembly completes
            output.Clear();
            input.AddItem(IronIngot, (MyFixedPoint)7);
            input.AddItem(GoldIngot, (MyFixedPoint)1);
            RunTicks(120);
            Update(discover);

            Assert.That(IsDiscovering(asm), Is.False);
            Assert.That(state.Mode, Is.EqualTo(MyAssemblerMode.Disassembly), "disassembly mode restored");
            Assert.That(state.Queue.Count, Is.EqualTo(1), "user's disassembly job re-added manually (no replacement in disassembly mode)");
            Assert.That((double)state.Queue[0].Amount, Is.EqualTo(4.0), "re-added at the original amount");
        }

        [Test]
        public void AsmLearn_Registry_RoundTrips()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var learnType = typeof(IngameScript.Program).GetNestedType("AsmLearn", System.Reflection.BindingFlags.NonPublic);

            // record a composition
            var comp = new Dictionary<MyItemType, MyFixedPoint> { [IronIngot] = (MyFixedPoint)7, [GoldIngot] = (MyFixedPoint)1 };
            learnType.GetMethod("record", flags).Invoke(null, new object[] { SteelPlate, comp });

            // serialize
            var registry = (string)learnType.GetMethod("writeRegistry", flags).Invoke(null, null);
            Assert.That(registry, Does.Contain("MyObjectBuilder_Component/SteelPlate;MyObjectBuilder_Ingot/Iron;7,MyObjectBuilder_Ingot/Gold;1"),
                "one line per item: all ingredients comma-separated on the same line");
            Assert.That(registry.Split('\n').Count(l => l.Contains("MyObjectBuilder_Component/SteelPlate")), Is.EqualTo(1),
                "the item prefix must not be repeated per ingredient");

            // wipe and reload
            learnType.GetField("known", flags).SetValue(null, new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>());
            foreach (var line in registry.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                learnType.GetMethod("loadRegistryLine", flags).Invoke(null, new object[] { line });
            }
            var known = (Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>)learnType.GetField("known", flags).GetValue(null);
            Assert.That(known.ContainsKey(SteelPlate), Is.True, "round-trip must restore the composition");
            Assert.That((double)known[SteelPlate][IronIngot], Is.EqualTo(7.0));
            Assert.That((double)known[SteelPlate][GoldIngot], Is.EqualTo(1.0));
        }
    }
}
