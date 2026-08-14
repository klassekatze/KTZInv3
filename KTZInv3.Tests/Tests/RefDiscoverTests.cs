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
    /// Exercises RefDiscover, the refinery recipe discovery controller: it
    /// finds (refinery block type, ore) pairs whose conversion is unknown,
    /// takes an enabled refinery that accepts the ore out of normal
    /// management, locks it against the sorter, disables its conveyor system,
    /// flushes it, stuffs it with the unknown ore, and lets RefLearn observe.
    /// Once the pattern is learned the refinery is released (UseConveyors
    /// restored, unlocked) and the registry written to CustomData.
    /// </summary>
    [TestFixture]
    public class RefDiscoverTests
    {
        static readonly MyItemType IronOre = new MyItemType("MyObjectBuilder_Ore", "Iron");
        static readonly MyItemType IronIngot = new MyItemType("MyObjectBuilder_Ingot", "Iron");
        static readonly MyDefinitionId LargeRefineryDef = new MyDefinitionId(typeof(MyObjectBuilder_Refinery), "LargeRefinery");

        IngameScript.Program _program;

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ore", "Iron", 0.00037f, 1.0f, (MyFixedPoint)1000000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Iron", 0.00027f, 1.0f, (MyFixedPoint)1000000);
            ResetStatics();

            _program = Gateway.CreateProgram().Build();
            IngameScript.Program.gProgram = _program;
            IngameScript.Program.APIWC = new IngameScript.WcPbApi();
            IngameScript.Program.tick = 0;

            var pType = typeof(IngameScript.Program);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            // gInv + managers needed by the pipeline (expel/force_retrieve call
            // gInv.rerrlog, genstatus dereferences the managers)
            var gInvField = pType.GetField("gInv", flags);
            if (gInvField != null) gInvField.SetValue(null, new IngameScript.Program.Inventory());
            SetStatic(pType, flags, "gAssemblerMgr");
            SetStatic(pType, flags, "gRefineryMgr");
            SetStatic(pType, flags, "gAutocraft");
            SetStatic(pType, flags, "gReactorMgr");
            // clear the refineries list (private static, set by ResourceLoader in-game)
            var refineriesField = pType.GetField("refineries", flags);
            if (refineriesField != null)
                refineriesField.SetValue(null, new List<IMyRefinery>());
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
            // clear RefLearn's registry + learner registry (learned dict is
            // def-keyed now)
            var refLearnType = typeof(IngameScript.Program).GetNestedType("RefLearn", System.Reflection.BindingFlags.NonPublic);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            refLearnType.GetField("learned", flags).SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>());
            refLearnType.GetField("consumedTotal", flags).SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, MyFixedPoint>>());
            refLearnType.GetField("producedTotal", flags).SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>());
            var learnerListType = typeof(System.Collections.Generic.List<>).MakeGenericType(refLearnType);
            refLearnType.GetField("allLearners", flags).SetValue(null, Activator.CreateInstance(learnerListType));

            // reset the RefDiscover static state (private statics)
            var rdType = typeof(IngameScript.Program).GetNestedType("RefDiscover", System.Reflection.BindingFlags.NonPublic);
            rdType.GetField("discRefinery", flags).SetValue(null, null);

            IngameScript.Program.Inventory.globalManifest.stuff.Clear();
            IngameScript.Program.Inventory.globalManifest.maxVolume = 0;
            IngameScript.Program.Inventory.globalManifest.freeVolume = 0;
            IngameScript.Program.Inventory.globalManifest.typeVolume.Clear();
            IngameScript.Program.Inventory.BlockInventory.bPriorityList.Clear();
            IngameScript.Program.Inventory.BlockInventory.bIDict.Clear();
            IngameScript.Program.Inventory.BlockInventory.idl = 0;
        }

        /// <summary>An enabled refinery fake that accepts only the given ores.</summary>
        static (IMyRefinery refinery, FakeInventory input, FakeInventory output) MakeRefinery(params MyItemType[] acceptedOres)
        {
            var input = new FakeInventory((MyFixedPoint)50.0) { AcceptedTypes = new HashSet<MyItemType>(acceptedOres) };
            var output = new FakeInventory((MyFixedPoint)50.0);
            var refinery = A.Fake<IMyRefinery>();
            A.CallTo(() => refinery.InputInventory).Returns(input);
            A.CallTo(() => refinery.OutputInventory).Returns(output);
            A.CallTo(() => refinery.BlockDefinition).Returns((SerializableDefinitionId)LargeRefineryDef);
            A.CallTo(() => refinery.Enabled).Returns(true);
            A.CallTo(() => refinery.CustomName).Returns("Refinery");
            A.CallTo(() => refinery.IsProducing).Returns(false);
            // the inventory pipeline links blocks through InventoryCount +
            // GetInventory(i); without these stubs the refinery's inventories
            // never join the transfer graph and force_retrieve has no target
            A.CallTo(() => refinery.InventoryCount).Returns(2);
            A.CallTo(() => refinery.GetInventory(0)).Returns(input);
            A.CallTo(() => refinery.GetInventory(1)).Returns(output);
            return (refinery, input, output);
        }

        /// <summary>
        /// Runs the real inventory pipeline over the given blocks so the global
        /// manifest and bPriorityList are populated (expel/force_retrieve need
        /// them), then ticks to the next 1s boundary so RefDiscover's scan runs.
        /// Sorting (updateT) is disabled: these tests exercise RefDiscover, and
        /// the sorter would drain the fake inventories and inflate the static
        /// manifest across the run.
        /// </summary>
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

        static void SetRefineries(List<IMyRefinery> list)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            typeof(IngameScript.Program).GetField("refineries", flags)
                .SetValue(null, list);
        }

        static object MakeDiscover()
            => Activator.CreateInstance(typeof(IngameScript.Program).GetNestedType("RefDiscover", System.Reflection.BindingFlags.NonPublic), nonPublic: true);

        static void Update(object discover)
            => typeof(IngameScript.Program).GetNestedType("RefDiscover", System.Reflection.BindingFlags.NonPublic)
                .GetMethod("update").Invoke(discover, null);

        static bool IsDiscovering(IMyRefinery r)
            => (bool)typeof(IngameScript.Program).GetNestedType("RefDiscover", System.Reflection.BindingFlags.NonPublic)
                .GetMethod("isDiscovering", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { (IMyCubeBlock)r });

        [Test]
        public void UnknownOre_WithEnoughStock_StartsDiscovery()
        {
            // cargo holds 5000 iron ore; refinery accepts iron; recipe unknown
            var cargo = CargoFactory.CreateCargo("2 Cargo [Ore].P999", (MyFixedPoint)10.0, (IronOre, (MyFixedPoint)5000));
            var (refinery, input, output) = MakeRefinery(IronOre);

            SetRefineries(new List<IMyRefinery> { refinery });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, refinery });

            var discover = MakeDiscover();
            Update(discover);

            Assert.That(IsDiscovering(refinery), Is.True, "unknown recipe with 5000 ore must start discovery");
            Assert.That((double)input.AmountOf(IronOre), Is.GreaterThan(0.0),
                "refinery must be stuffed with the unknown ore");
            Assert.That(refinery.UseConveyorSystem, Is.False,
                "refinery's own conveyor auto-move must be disabled during discovery");
            // locked against the sorter
            var bi = IngameScript.Program.Inventory.BlockInventory.getBI(refinery);
            Assert.That(bi.locked, Is.True, "discovering refinery must be locked to the sorter");
        }

        [Test]
        public void NotEnoughStock_DoesNotStartDiscovery()
        {
            var cargo = CargoFactory.CreateCargo("2 Cargo [Ore].P999", (MyFixedPoint)10.0, (IronOre, (MyFixedPoint)1000));
            var (refinery, input, output) = MakeRefinery(IronOre);

            SetRefineries(new List<IMyRefinery> { refinery });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, refinery });

            var discover = MakeDiscover();
            Update(discover);

            Assert.That(IsDiscovering(refinery), Is.False, "1000 ore is below the 3000 threshold");
            Assert.That(input.AmountOf(IronOre), Is.EqualTo((MyFixedPoint)0));
        }

        [Test]
        public void KnownRecipe_DoesNotStartDiscovery()
        {
            var cargo = CargoFactory.CreateCargo("2 Cargo [Ore].P999", (MyFixedPoint)10.0, (IronOre, (MyFixedPoint)5000));
            var (refinery, input, output) = MakeRefinery(IronOre);

            // recipe already known for this refinery def
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var learned = new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>();
            learned[LargeRefineryDef] = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();
            learned[LargeRefineryDef][IronOre] = new Dictionary<MyItemType, MyFixedPoint> { [IronIngot] = (MyFixedPoint)0.7 };
            typeof(IngameScript.Program).GetNestedType("RefLearn", System.Reflection.BindingFlags.NonPublic)
                .GetField("learned", flags).SetValue(null, learned);

            SetRefineries(new List<IMyRefinery> { refinery });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, refinery });

            var discover = MakeDiscover();
            Update(discover);

            Assert.That(IsDiscovering(refinery), Is.False, "known recipe must not be re-discovered");
            Assert.That(input.AmountOf(IronOre), Is.EqualTo((MyFixedPoint)0));
        }

        [Test]
        public void RefineryThatDoesNotAcceptOre_DoesNotStartDiscovery()
        {
            var cargo = CargoFactory.CreateCargo("2 Cargo [Ore].P999", (MyFixedPoint)10.0, (IronOre, (MyFixedPoint)5000));
            // refinery accepts nothing (no whitelist = no accepted items in the mock)
            var (refinery, input, output) = MakeRefinery();

            SetRefineries(new List<IMyRefinery> { refinery });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, refinery });

            var discover = MakeDiscover();
            Update(discover);

            Assert.That(IsDiscovering(refinery), Is.False, "refinery that doesn't accept the ore must not be used");
        }

        [Test]
        public void Release_RestoresRefineryAndWritesRegistry()
        {
            var cargo = CargoFactory.CreateCargo("2 Cargo [Ore].P999", (MyFixedPoint)10.0, (IronOre, (MyFixedPoint)5000));
            var (refinery, input, output) = MakeRefinery(IronOre);

            SetRefineries(new List<IMyRefinery> { refinery });
            RunPipelineAndScan(new List<IMyTerminalBlock> { cargo.Block, refinery });

            var discover = MakeDiscover();
            Update(discover);
            Assert.That(IsDiscovering(refinery), Is.True);

            // the learner cracks the recipe (refinery consumed 70 iron, made 49)
            input.Clear();
            output.AddItem(IronIngot, (MyFixedPoint)49);
            // (input was stuffed with iron by discovery; simulate consumption)
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var learned = new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>();
            learned[LargeRefineryDef] = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();
            learned[LargeRefineryDef][IronOre] = new Dictionary<MyItemType, MyFixedPoint> { [IronIngot] = (MyFixedPoint)0.7 };
            typeof(IngameScript.Program).GetNestedType("RefLearn", System.Reflection.BindingFlags.NonPublic)
                .GetField("learned", flags).SetValue(null, learned);

            // next update sees the learned recipe and releases
            Update(discover);

            Assert.That(IsDiscovering(refinery), Is.False, "learned recipe must release the refinery");
            Assert.That(refinery.UseConveyorSystem, Is.True, "UseConveyors must be restored on release");
            var bi = IngameScript.Program.Inventory.BlockInventory.getBI(refinery);
            Assert.That(bi.locked, Is.False, "refinery must be unlocked on release");
            // registry written to CustomData (Autocraft.writeCD)
            Assert.That(_program.Me.CustomData, Does.Contain("KTZREF;"), "release must write the refinery registry to CustomData");
        }
    }
}
