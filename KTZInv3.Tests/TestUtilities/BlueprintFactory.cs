using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using FakeItEasy;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>One cargo container parsed from a blueprint.</summary>
    public sealed class BlueprintCargo
    {
        public string Name;
        public long EntityId;
        public MyFixedPoint MaxVolume;
        public List<(MyItemType type, MyFixedPoint amount)> Items = new List<(MyItemType, MyFixedPoint)>();
        public int GridIndex;
    }

    /// <summary>
    /// Parses a Space Engineers blueprint (.sbc) and generates the full mock
    /// world for it: one grid per CubeGrid in the file, a fake cargo container
    /// block per MyObjectBuilder_CargoContainer (with its exact CustomName and
    /// inventory contents), a FakeGts holding everything, and a Me on the
    /// specified grid. Item subtypes not in the built-in registry are
    /// auto-registered with realistic volumes.
    ///
    /// Blueprints contain the SAME data the game loads: cargo blocks carry a
    /// ComponentContainer > MyInventoryBase > MyObjectBuilder_Inventory with
    /// Items. The parser mirrors that structure directly.
    /// </summary>
    public static class BlueprintFactory
    {
        static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

        public sealed class World
        {
            public FakeGts Gts = new FakeGts();
            public IMyProgrammableBlock Me;
            public List<IMyCubeGrid> Grids = new List<IMyCubeGrid>();
            public List<CargoMock> Cargos = new List<CargoMock>();
            public List<BlueprintCargo> BlueprintCargos = new List<BlueprintCargo>();
            public int MeGridIndex;
        }

        /// <summary>
        /// Loads a blueprint and builds mocks for every cargo container in it.
        /// </summary>
        /// <param name="sbcPath">Path to the .sbc blueprint file.</param>
        /// <param name="meGridIndex">Which CubeGrid the PB lives on (defaults to
        /// the grid with the most cargo containers).</param>
        public static World Load(string sbcPath, int meGridIndex = -1)
        {
            ItemDefinitions.EnsureRegistered();
            var doc = XDocument.Load(sbcPath);
            var gridElements = doc.Descendants("CubeGrid").ToList();

            var world = new World();
            if (meGridIndex < 0)
            {
                // pick the grid with the most cargo containers as "the base"
                int best = 0, bestCount = -1;
                for (int i = 0; i < gridElements.Count; i++)
                {
                    var cargos = gridElements[i].Descendants("MyObjectBuilder_CubeBlock")
                        .Where(b => (string)b.Attribute(Xsi + "type") == "MyObjectBuilder_CargoContainer")
                        .Count();
                    if (cargos > bestCount) { bestCount = cargos; best = i; }
                }
                meGridIndex = best;
            }
            world.MeGridIndex = meGridIndex;

            for (int gi = 0; gi < gridElements.Count; gi++)
            {
                var grid = A.Fake<IMyCubeGrid>();
                var entityId = (long?)gridElements[gi].Element("EntityId") ?? 0;
                A.CallTo(() => grid.EntityId).Returns(entityId);
                world.Grids.Add(grid);

                foreach (var blockEl in gridElements[gi].Descendants("MyObjectBuilder_CubeBlock"))
                {
                    if ((string)blockEl.Attribute(Xsi + "type") != "MyObjectBuilder_CargoContainer") continue;

                    var cargo = ParseCargo(blockEl, gi);
                    world.BlueprintCargos.Add(cargo);

                    // build the fake block + inventory from the parsed data
                    var inv = new FakeInventory(cargo.MaxVolume);
                    foreach (var (type, amount) in cargo.Items)
                        inv.AddItem(type, amount);

                    var block = A.Fake<IMyTerminalBlock>();
                    A.CallTo(() => block.CustomName).Returns(cargo.Name);
                    A.CallTo(() => block.CustomData).Returns("");
                    A.CallTo(() => block.CubeGrid).Returns(grid);
                    A.CallTo(() => block.DefinitionDisplayNameText).Returns("Large Container");
                    A.CallTo(() => block.InventoryCount).Returns(1);
                    A.CallTo(() => block.GetInventory(0)).Returns(inv);
                    A.CallTo(() => block.GetInventory(A<int>.That.Matches(i => i != 0))).Returns(null);
                    A.CallTo(() => block.GetOwnerFactionTag()).Returns("FACTION");
                    A.CallTo(() => block.IsWorking).Returns(true);
                    A.CallTo(() => block.IsFunctional).Returns(true);
                    A.CallTo(() => block.IsSameConstructAs(A<IMyTerminalBlock>.Ignored)).Returns(true);
                    A.CallTo(() => block.HasInventory).Returns(true);
                    A.CallTo(() => block.HasPlayerAccess(A<long>.Ignored)).Returns(true);
                    A.CallTo(() => block.EntityId).Returns(cargo.EntityId);
                    A.CallTo(() => block.GetProperty("WcPbAPI")).Returns(null);

                    world.Cargos.Add(new CargoMock(block, inv, grid));
                    world.Gts.Blocks.Add(block);
                }
            }

            world.Me = MeFactory.CreateMe(world.Grids[meGridIndex]);
            return world;
        }

        static BlueprintCargo ParseCargo(XElement blockEl, int gridIndex)
        {
            var cargo = new BlueprintCargo { GridIndex = gridIndex };
            cargo.Name = (string)blockEl.Element("CustomName") ?? "Unnamed Cargo";
            cargo.EntityId = (long?)blockEl.Element("EntityId") ?? 0;
            cargo.MaxVolume = (MyFixedPoint)0.0;

            // ComponentContainer > Components > ComponentData (TypeId=MyInventoryBase) > Component > Items
            foreach (var compData in blockEl.Descendants("ComponentData"))
            {
                var typeId = (string)compData.Element("TypeId");
                if (typeId == null || !typeId.Contains("Inventory")) continue;
                var component = compData.Element("Component");
                if (component == null) continue;

                var vol = (double?)component.Element("Volume") ?? 0;
                cargo.MaxVolume = (MyFixedPoint)vol;

                foreach (var item in component.Descendants("MyObjectBuilder_InventoryItem"))
                {
                    var amount = (double?)item.Element("Amount") ?? 0;
                    if (amount <= 0) continue;
                    var pc = item.Element("PhysicalContent");
                    if (pc == null) continue;
                    var pcType = (string)pc.Attribute(Xsi + "type") ?? "MyObjectBuilder_PhysicalObject";
                    var subtype = (string)pc.Element("SubtypeName") ?? "Unknown";
                    RegisterDefinition(pcType, subtype);
                    var itemType = new MyItemType(pcType, subtype);
                    cargo.Items.Add((itemType, (MyFixedPoint)amount));
                }
            }
            return cargo;
        }

        /// <summary>Registers a definition for an item subtype if not already known.</summary>
        static void RegisterDefinition(string typeId, string subtype)
        {
            // realistic-ish volumes: ore 0.37 L/kg, ingot 0.27 L/kg, components 0.1 L, bottles/ammo 1 L
            float vol = 0.001f;
            float mass = 1.0f;
            MyFixedPoint maxStack = (MyFixedPoint)1000000;
            if (typeId == "MyObjectBuilder_Ore") { vol = 0.00037f; maxStack = (MyFixedPoint)1000000; }
            else if (typeId == "MyObjectBuilder_Ingot") { vol = 0.00027f; maxStack = (MyFixedPoint)1000000; }
            else if (typeId == "MyObjectBuilder_Component") { vol = 0.0001f; maxStack = (MyFixedPoint)1000; }
            else if (typeId == "MyObjectBuilder_AmmoMagazine") { vol = 0.001f; maxStack = (MyFixedPoint)1000; }
            else if (typeId == "MyObjectBuilder_GasContainerObject" || typeId == "MyObjectBuilder_OxygenContainerObject") { vol = 0.001f; maxStack = (MyFixedPoint)100; }

            try { ItemDefinitions.RegisterItem(typeId, subtype, vol, mass, maxStack); }
            catch { /* already registered or unknown builder type - ignore */ }
        }
    }
}
