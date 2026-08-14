using System;
using System.Collections.Generic;
using FakeItEasy;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// The single entry point tests use to prepare fake inventories: a cargo
    /// container mock with the name field (which KTZInv3 parses for priority and
    /// category tags), CustomData, a grid, a definition display name, and one or
    /// more real-behavior <see cref="FakeInventory"/> instances.
    ///
    /// The block itself is a FakeItEasy fake of <see cref="IMyTerminalBlock"/>
    /// (the real MyCargoContainer would need a full grid/entity hierarchy);
    /// the inventory is a real fake with working transfer semantics.
    /// </summary>
    public static class CargoFactory
    {
        static long _gridId = 1000;

        /// <summary>
        /// Creates a fake cargo container block.
        /// </summary>
        /// <param name="name">CustomName — KTZInv3 parses priority ([p99]) and
        /// category ([Components]) tokens from this.</param>
        /// <param name="maxVolume">Inventory capacity in m^3 (e.g. 5.0 for a large cargo).</param>
        /// <param name="items">Initial contents as (itemType, amount) pairs.</param>
        public static CargoMock CreateCargo(string name, MyFixedPoint maxVolume, params (MyItemType type, MyFixedPoint amount)[] items)
        {
            return CreateCargo(name, maxVolume, null, null, items);
        }

        /// <summary>
        /// Creates a fake cargo container block with a real read/write CustomData
        /// (used by special containers — the script parses stocktargets from it
        /// AND writes to it: the ISYCOMPAT header prefix and the empty-special
        /// auto-generated manifest).
        /// </summary>
        public static CargoMock CreateCargo(string name, string customData, MyFixedPoint maxVolume, params (MyItemType type, MyFixedPoint amount)[] items)
        {
            return CreateCargo(name, maxVolume, null, customData, items);
        }

        /// <summary>
        /// Creates a fake cargo container block on a specific grid (pass the grid
        /// from <see cref="CreateGrid"/> so it matches Program.Me's grid).
        /// </summary>
        public static CargoMock CreateCargo(string name, MyFixedPoint maxVolume, IMyCubeGrid grid, params (MyItemType type, MyFixedPoint amount)[] items)
        {
            return CreateCargo(name, maxVolume, grid, null, items);
        }

        static CargoMock CreateCargo(string name, MyFixedPoint maxVolume, IMyCubeGrid grid, string customData, params (MyItemType type, MyFixedPoint amount)[] items)
        {
            ItemDefinitions.EnsureRegistered();

            if (grid == null)
            {
                grid = A.Fake<IMyCubeGrid>();
                A.CallTo(() => grid.EntityId).Returns(_gridId++);
            }

            var inventory = new FakeInventory(maxVolume);
            foreach (var (type, amount) in items)
                inventory.AddItem(type, amount);

            // real read/write CustomData: a captured variable behind the getter
            // AND a setter that stores into it (the script writes CustomData for
            // special containers — the ISYCOMPAT prefix and auto-generated
            // manifest — so the fake must accept those writes and return them).
            var customDataValue = customData ?? "";
            var block = A.Fake<IMyTerminalBlock>();
            A.CallTo(() => block.CustomName).Returns(name);
            A.CallTo(() => block.CustomData).ReturnsLazily(() => { ApiCost.Apply(ApiOp.CustomDataGet); return customDataValue; });
            A.CallToSet(() => block.CustomData).Invokes((string v) => customDataValue = v);
            A.CallTo(() => block.CubeGrid).Returns(grid);
            A.CallTo(() => block.DefinitionDisplayNameText).Returns("Large Container");
            A.CallTo(() => block.InventoryCount).Returns(1);
            A.CallTo(() => block.GetInventory(0)).ReturnsLazily(() => { ApiCost.Apply(ApiOp.BlockGetInventory); return inventory; });
            A.CallTo(() => block.GetInventory(A<int>.That.Matches(i => i != 0))).Returns(null);
            A.CallTo(() => block.GetOwnerFactionTag()).Returns("FACTION");
            A.CallTo(() => block.IsWorking).Returns(true);
            A.CallTo(() => block.IsFunctional).Returns(true);
            A.CallTo(() => block.IsSameConstructAs(A<IMyTerminalBlock>.Ignored)).Returns(true);
            A.CallTo(() => block.HasInventory).Returns(true);
            A.CallTo(() => block.HasPlayerAccess(A<long>.Ignored)).Returns(true);
            A.CallTo(() => block.EntityId).Returns(_gridId++);
            // no WeaponCore API on a plain cargo container
            A.CallTo(() => block.GetProperty("WcPbAPI")).Returns(null);

            return new CargoMock(block, inventory, grid);
        }

        /// <summary>Creates a fresh grid the Me mock and cargo blocks can share.</summary>
        public static IMyCubeGrid CreateGrid()
        {
            var grid = A.Fake<IMyCubeGrid>();
            A.CallTo(() => grid.EntityId).Returns(_gridId++);
            return grid;
        }

        /// <summary>
        /// Creates a fake cargo container with multiple inventories (e.g. an
        /// assembler-like block with input + output).
        /// </summary>
        public static CargoMock CreateMultiInventory(string name, params (IMyInventory inv, MyFixedPoint maxVolume)[] inventories)
        {
            ItemDefinitions.EnsureRegistered();

            var grid = A.Fake<IMyCubeGrid>();
            A.CallTo(() => grid.EntityId).Returns(_gridId++);

            var block = A.Fake<IMyTerminalBlock>();
            A.CallTo(() => block.CustomName).Returns(name);
            A.CallTo(() => block.CustomData).Returns("");
            A.CallTo(() => block.CubeGrid).Returns(grid);
            A.CallTo(() => block.DefinitionDisplayNameText).Returns("Large Container");
            A.CallTo(() => block.InventoryCount).Returns(inventories.Length);
            for (int i = 0; i < inventories.Length; i++)
            {
                var idx = i;
                var inv = inventories[idx].inv;
                A.CallTo(() => block.GetInventory(idx)).Returns(inv);
            }
            A.CallTo(() => block.GetInventory(A<int>.That.Matches(i => i < 0 || i >= inventories.Length))).Returns(null);
            A.CallTo(() => block.GetOwnerFactionTag()).Returns("FACTION");
            A.CallTo(() => block.IsWorking).Returns(true);
            A.CallTo(() => block.IsFunctional).Returns(true);
            A.CallTo(() => block.GetProperty("WcPbAPI")).Returns(null);

            return new CargoMock(block, inventories[0].inv, grid);
        }
    }

    /// <summary>
    /// The mock bundle: the block fake, its primary inventory, and the grid.
    /// Tests reach the inventory through <see cref="Inventory"/> to assert
    /// contents after sorting runs.
    /// </summary>
    public sealed class CargoMock
    {
        public IMyTerminalBlock Block { get; }
        public IMyInventory Inventory { get; }
        public IMyCubeGrid Grid { get; }

        public CargoMock(IMyTerminalBlock block, IMyInventory inventory, IMyCubeGrid grid)
        {
            Block = block;
            Inventory = inventory;
            Grid = grid;
        }

        public FakeInventory AsFakeInventory() => (FakeInventory)Inventory;

        /// <summary>Convenience: how many of the given type are in the primary inventory.</summary>
        public MyFixedPoint AmountOf(MyItemType type) => ((FakeInventory)Inventory).AmountOf(type);
    }
}
