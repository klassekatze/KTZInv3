using System;
using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// A headless <see cref="IMyGridTerminalSystem"/>: holds the blocks a test
    /// registers and implements <see cref="GetBlocksOfType{T}"/> with real
    /// filtering (type check + predicate), which is all the script's block
    /// loader and recalcInvBlocks use. Unused members throw so a test that
    /// accidentally depends on them fails loudly instead of silently returning
    /// garbage.
    /// </summary>
    public class FakeGts : IMyGridTerminalSystem
    {
        /// <summary>All blocks visible to the "grid". Add your mocks here.</summary>
        public readonly List<IMyTerminalBlock> Blocks = new List<IMyTerminalBlock>();

        public void GetBlocks(List<IMyTerminalBlock> blocks)
        {
            ApiCost.Apply(ApiOp.GtsGetBlocks);
            blocks.AddRange(Blocks);
        }

        public void GetBlocksOfType<T>(List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null) where T : class
        {
            ApiCost.Apply(ApiOp.GtsGetBlocks);
            foreach (var b in Blocks)
                if (b is T && (collect == null || collect(b)))
                    blocks.Add(b);
        }

        public void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class
        {
            ApiCost.Apply(ApiOp.GtsGetBlocks);
            foreach (var b in Blocks)
                if (b is T tb && (collect == null || collect(tb)))
                    blocks.Add(tb);
        }

        public void GetBlockGroups(List<IMyBlockGroup> blockGroups, Func<IMyBlockGroup, bool> collect = null)
            => throw new NotImplementedException("FakeGts.GetBlockGroups");

        public void SearchBlocksOfName(string name, List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null)
        {
            foreach (var b in Blocks)
                if (b.CustomName.Contains(name) && (collect == null || collect(b)))
                    blocks.Add(b);
        }

        public IMyTerminalBlock GetBlockWithName(string name)
        {
            foreach (var b in Blocks)
                if (b.CustomName == name) return b;
            return null;
        }

        public IMyBlockGroup GetBlockGroupWithName(string name)
            => throw new NotImplementedException("FakeGts.GetBlockGroupWithName");

        public IMyTerminalBlock GetBlockWithId(long id)
        {
            foreach (var b in Blocks)
                if (b.EntityId == id) return b;
            return null;
        }

        public bool CanAccess(IMyTerminalBlock block, MyTerminalAccessScope scope = MyTerminalAccessScope.All) => true;
        public bool CanAccess(IMyCubeGrid grid, MyTerminalAccessScope scope = MyTerminalAccessScope.All) => true;
    }
}
