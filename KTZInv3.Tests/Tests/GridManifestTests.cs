using System.Collections.Generic;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// gridManifest: only blocks on the SAME grid as the PB contribute; blocks
    /// on other grids (docked ships, subgrids) are in globalManifest but NOT in
    /// gridManifest. The assembler stock-quota code reads gridManifest so docked
    /// cargo cannot change assembly/disassembly decisions.
    /// </summary>
    [TestFixture]
    public class GridManifestTests
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
        public void GlobalManifest_IncludesDockedAndOwnGrid()
        {
            var meGrid = CargoFactory.CreateGrid();
            var dockGrid = CargoFactory.CreateGrid();
            var meBlock = MeFactory.CreateMe(meGrid);

            var own = CargoFactory.CreateCargo("Own [Components] [P100]", (MyFixedPoint)10.0, meGrid,
                (SteelPlate, (MyFixedPoint)600));
            var docked = CargoFactory.CreateCargo("Docked [Components] [P100]", (MyFixedPoint)10.0, dockGrid,
                (Motor, (MyFixedPoint)300));

            var builder = Gateway.CreateProgram().WithMe(meBlock);
            _program = builder.Build();
            IngameScript.Program.APIWC = new IngameScript.WcPbApi();
            IngameScript.Program.tick = 0;

            RunPipeline(new List<IMyTerminalBlock> { own.Block, docked.Block });

            MyFixedPoint globalSteel, globalMotor, gridSteel, gridMotor;
            IngameScript.Program.Inventory.globalManifest.stuff.TryGetValue(SteelPlate, out globalSteel);
            IngameScript.Program.Inventory.globalManifest.stuff.TryGetValue(Motor, out globalMotor);
            IngameScript.Program.Inventory.gridManifest.stuff.TryGetValue(SteelPlate, out gridSteel);
            IngameScript.Program.Inventory.gridManifest.stuff.TryGetValue(Motor, out gridMotor);

            Assert.That((double)globalSteel, Is.EqualTo(600.0), "global must include own-grid steel");
            Assert.That((double)globalMotor, Is.EqualTo(300.0), "global must include docked motor");
            Assert.That((double)gridSteel, Is.EqualTo(600.0), "grid manifest must include own-grid steel");
            Assert.That((double)gridMotor, Is.EqualTo(0.0),
                "grid manifest must NOT include docked-ship content");
        }

        [Test]
        public void GridManifest_ExcludesDockedShip()
        {
            var meGrid = CargoFactory.CreateGrid();
            var dockGrid = CargoFactory.CreateGrid();
            var meBlock = MeFactory.CreateMe(meGrid);

            var own = CargoFactory.CreateCargo("Own [Components] [P100]", (MyFixedPoint)10.0, meGrid,
                (SteelPlate, (MyFixedPoint)600));
            var docked = CargoFactory.CreateCargo("Docked [Components] [P100]", (MyFixedPoint)10.0, dockGrid,
                (Motor, (MyFixedPoint)300));

            var builder = Gateway.CreateProgram().WithMe(meBlock);
            _program = builder.Build();
            IngameScript.Program.APIWC = new IngameScript.WcPbApi();
            IngameScript.Program.tick = 0;

            var inv = RunPipeline(new List<IMyTerminalBlock> { own.Block, docked.Block });

            MyFixedPoint gridMotor;
            IngameScript.Program.Inventory.gridManifest.stuff.TryGetValue(Motor, out gridMotor);
            Assert.That((double)gridMotor, Is.EqualTo(0.0),
                "docked-ship items must never appear in gridManifest");
        }
    }
}