using System;
using System.Collections.Generic;
using FakeItEasy;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using VRage.Game.ModAPI.Ingame;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// Builds the fake programmable block (Program.Me): same grid and faction as
    /// the cargo blocks, a stable OwnerId, Closed=false, and a "WcPbAPI"
    /// terminal property backed by <see cref="WcPbApiMocker.Delegates"/> so the
    /// script's WcPbApi.Activate() succeeds and the block loader can run.
    /// </summary>
    public static class MeFactory
    {
        public static IMyProgrammableBlock CreateMe(IMyCubeGrid grid, string faction = "FACTION", long ownerId = 42)
        {
            var property = A.Fake<ITerminalProperty<IReadOnlyDictionary<string, Delegate>>>();
            A.CallTo(() => property.GetValue(A<IMyCubeBlock>.Ignored)).Returns(WcPbApiMocker.Delegates);

            var me = A.Fake<IMyProgrammableBlock>();
            A.CallTo(() => me.CubeGrid).Returns(grid);
            A.CallTo(() => me.EntityId).Returns(1001L);
            A.CallTo(() => me.OwnerId).Returns(ownerId);
            A.CallTo(() => me.GetOwnerFactionTag()).Returns(faction);
            A.CallTo(() => me.Closed).Returns(false);
            A.CallTo(() => me.CustomData).Returns("");
            A.CallTo(() => me.CustomName).Returns("PB");
            A.CallTo(() => me.GetProperty("WcPbAPI")).Returns(property);
            return me;
        }
    }
}
