using System.Collections.Generic;
using FakeItEasy;
using NUnit.Framework;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using KTZInv3.Tests.TestUtilities;

namespace KTZInv3.Tests.Tests
{
    /// <summary>
    /// Exercises the reactor fuel rebalancer (ReactorMgr.update) against mocked
    /// reactors, focusing on the cases the sorter tests can't reach: fuel
    /// grouping by type, empty-reactor assignment via GetAcceptedItems, and the
    /// constrained-receiver cap (a receiver whose inventory capacity is smaller
    /// than the group average must never book phantom fuel).
    /// </summary>
    [TestFixture]
    public class ReactorBalanceTests
    {
        static readonly MyItemType Uranium = new MyItemType("MyObjectBuilder_Ingot", "Uranium");
        static readonly MyItemType FusionFuel = new MyItemType("MyObjectBuilder_Component", "sdx_itemReactorFuel");

        IngameScript.Program _program;

        [SetUp]
        public void SetUp()
        {
            ItemDefinitions.EnsureRegistered();
            ItemDefinitions.RegisterItem("MyObjectBuilder_Ingot", "Uranium", 0.00027f, 1.0f, (MyFixedPoint)100000);
            ItemDefinitions.RegisterItem("MyObjectBuilder_Component", "sdx_itemReactorFuel", 0.0001f, 1.0f, (MyFixedPoint)1000);

            // fresh manager statics
            var pType = typeof(IngameScript.Program);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var reactorsField = pType.GetField("reactors", flags);
            reactorsField.SetValue(null, new List<IMyReactor>());
            var mgrField = pType.GetField("gReactorMgr", flags);
            mgrField.SetValue(null, mgrField.FieldType.GetConstructor(System.Type.EmptyTypes).Invoke(null));

            _program = Gateway.CreateProgram().Build();
            IngameScript.Program.gProgram = _program;
            IngameScript.Program.APIWC = new IngameScript.WcPbApi(); // HasCoreWeapon -> false, avoids NRE in updateP
            IngameScript.Program._ticks = 180; // reactor update cadence (60*3)
            IngameScript.Program.tick = 180;
        }

        /// <summary>Creates a reactor block mock whose inventory has the given
        /// capacity and accepts the given fuel types.</summary>
        static IMyReactor MakeReactor(MyFixedPoint maxVolume, params MyItemType[] acceptedFuels)
        {
            var inv = new FakeInventory(maxVolume);
            inv.AcceptedTypes = new HashSet<MyItemType>(acceptedFuels);
            var reactor = A.Fake<IMyReactor>();
            A.CallTo(() => reactor.GetInventory()).Returns(inv);
            A.CallTo(() => reactor.GetInventory(0)).Returns(inv);
            return reactor;
        }

        static List<IMyReactor> Reactors()
            => (List<IMyReactor>)typeof(IngameScript.Program)
                .GetField("reactors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .GetValue(null);

        static void RunUpdate()
        {
            var mgr = typeof(IngameScript.Program)
                .GetField("gReactorMgr", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .GetValue(null);
            mgr.GetType().GetMethod("update").Invoke(mgr, null);
        }

        [Test]
        public void ConstrainedReceiver_IsCappedAtItsFreeSpace_NoPhantomFuel()
        {
            // two large reactors (room for ~3700 uranium each at 0.00027 m3/kg)
            // and one tiny reactor that can only hold ~3.7 kg. The group average
            // is ~66; the tiny one must receive only what physically fits, and
            // the donors must not book more than they actually gave.
            var big1 = MakeReactor((MyFixedPoint)1.0, Uranium);
            var big2 = MakeReactor((MyFixedPoint)1.0, Uranium);
            var small = MakeReactor((MyFixedPoint)0.001, Uranium); // ~3.7 kg capacity

            ((FakeInventory)big1.GetInventory()).AddItem(Uranium, (MyFixedPoint)100);
            ((FakeInventory)big2.GetInventory()).AddItem(Uranium, (MyFixedPoint)100);

            Reactors().AddRange(new[] { big1, big2, small });
            RunUpdate();

            var smallInv = (FakeInventory)small.GetInventory();
            var big1Inv = (FakeInventory)big1.GetInventory();
            var big2Inv = (FakeInventory)big2.GetInventory();

            // tiny reactor must not exceed what its volume can hold
            // (0.001 m3 / 0.00027 m3 per unit = 3.7037; allow the mock's
            // fixed-point rounding overshoot of ~0.1%)
            Assert.That((double)smallInv.AmountOf(Uranium), Is.LessThanOrEqualTo(3.71),
                "constrained receiver received more fuel than its inventory can hold");
            Assert.That((double)smallInv.AmountOf(Uranium), Is.GreaterThan(0.0),
                "constrained receiver should have received the fuel that DOES fit");

            // no phantom fuel: total across the group is conserved
            double total = (double)big1Inv.AmountOf(Uranium)
                         + (double)big2Inv.AmountOf(Uranium)
                         + (double)smallInv.AmountOf(Uranium);
            Assert.That(total, Is.EqualTo(200.0).Within(0.001),
                "reactor rebalance lost or created fuel (phantom bookkeeping)");
        }

        [Test]
        public void EmptyReactor_JoinsGroupByAcceptedItems_MixedFuels()
        {
            // two reactors burn vanilla uranium, one burns fusion fuel; an empty
            // reactor that accepts ONLY uranium must join the uranium group and
            // get topped up from the uranium donors, not the fusion one. Donors
            // sit well above average+margin so the rebalance actually fires.
            var u1 = MakeReactor((MyFixedPoint)1.0, Uranium);
            var u2 = MakeReactor((MyFixedPoint)1.0, Uranium);
            var fusion = MakeReactor((MyFixedPoint)1.0, FusionFuel);
            var emptyUranium = MakeReactor((MyFixedPoint)1.0, Uranium);

            ((FakeInventory)u1.GetInventory()).AddItem(Uranium, (MyFixedPoint)100);
            ((FakeInventory)u2.GetInventory()).AddItem(Uranium, (MyFixedPoint)100);
            ((FakeInventory)fusion.GetInventory()).AddItem(FusionFuel, (MyFixedPoint)100);

            Reactors().AddRange(new[] { u1, u2, fusion, emptyUranium });
            RunUpdate();

            var emptyInv = (FakeInventory)emptyUranium.GetInventory();
            var fusionInv = (FakeInventory)fusion.GetInventory();

            Assert.That((double)emptyInv.AmountOf(Uranium), Is.GreaterThan(0.0),
                "empty uranium-accepting reactor should have been topped from uranium donors");
            Assert.That((double)fusionInv.AmountOf(FusionFuel), Is.EqualTo(100.0).Within(0.001),
                "fusion fuel must not be moved into the uranium reactor");
            Assert.That((double)emptyInv.AmountOf(FusionFuel), Is.EqualTo(0.0),
                "empty uranium reactor must not receive fusion fuel");
        }

        [Test]
        public void DifferentFuelTypes_AreBalancedIndependently()
        {
            // uranium group is imbalanced; fusion group is balanced. Balancing
            // uranium must not touch the fusion reactors at all.
            var u1 = MakeReactor((MyFixedPoint)1.0, Uranium);
            var u2 = MakeReactor((MyFixedPoint)1.0, Uranium);
            var f1 = MakeReactor((MyFixedPoint)1.0, FusionFuel);
            var f2 = MakeReactor((MyFixedPoint)1.0, FusionFuel);

            ((FakeInventory)u1.GetInventory()).AddItem(Uranium, (MyFixedPoint)90);
            ((FakeInventory)u2.GetInventory()).AddItem(Uranium, (MyFixedPoint)10);
            ((FakeInventory)f1.GetInventory()).AddItem(FusionFuel, (MyFixedPoint)50);
            ((FakeInventory)f2.GetInventory()).AddItem(FusionFuel, (MyFixedPoint)50);

            Reactors().AddRange(new[] { u1, u2, f1, f2 });
            RunUpdate();

            var f1Inv = (FakeInventory)f1.GetInventory();
            var f2Inv = (FakeInventory)f2.GetInventory();

            Assert.That((double)f1Inv.AmountOf(FusionFuel), Is.EqualTo(50.0).Within(0.001));
            Assert.That((double)f2Inv.AmountOf(FusionFuel), Is.EqualTo(50.0).Within(0.001));
            Assert.That((double)f1Inv.AmountOf(Uranium), Is.EqualTo(0.0));
        }

        [Test]
        public void ManageReactorsFalse_DisablesRebalance()
        {
            // MANAGE_REACTORS=false must short-circuit update(): no fuel moves,
            // even with a severe imbalance.
            var u1 = MakeReactor((MyFixedPoint)1.0, Uranium);
            var u2 = MakeReactor((MyFixedPoint)1.0, Uranium);
            ((FakeInventory)u1.GetInventory()).AddItem(Uranium, (MyFixedPoint)90);
            ((FakeInventory)u2.GetInventory()).AddItem(Uranium, (MyFixedPoint)10);
            Reactors().AddRange(new[] { u1, u2 });

            var pType = typeof(IngameScript.Program);
            pType.GetField("MANAGE_REACTORS", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .SetValue(null, false);
            try
            {
                RunUpdate();
                Assert.That((double)((FakeInventory)u1.GetInventory()).AmountOf(Uranium), Is.EqualTo(90.0));
                Assert.That((double)((FakeInventory)u2.GetInventory()).AmountOf(Uranium), Is.EqualTo(10.0));
            }
            finally
            {
                pType.GetField("MANAGE_REACTORS", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    .SetValue(null, true);
            }
        }

        [Test]
        public void ManageReactorsTrue_LocksReactorsInSorter()
        {
            // reactors must be locked (BlockInventory.locked) when reactor
            // management is on, so the sorter never moves fuel in/out of them.
            var reactor = MakeReactor((MyFixedPoint)1.0, Uranium);
            ((FakeInventory)reactor.GetInventory()).AddItem(Uranium, (MyFixedPoint)10);

            var bi = new IngameScript.Program.Inventory.BlockInventory(reactor);
            // updateP parses the block name; with no tags it would otherwise be
            // unlocked + holdall. The reactor lock must win.
            bi.updateP();
            Assert.That(bi.locked, Is.True, "reactor must be locked in the sorter when MANAGE_REACTORS is on");
        }
    }
}
