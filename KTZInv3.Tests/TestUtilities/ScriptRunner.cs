using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// Drives the real Program entry point: instantiates the script via the
    /// Gateway, wires a FakeGts + Me, then calls Main("", UpdateType.Update1)
    /// repeatedly — exactly like the game engine does every tick — until the
    /// inventory update counter reaches the requested value.
    ///
    /// Use <see cref="Program"/> and <see cref="GetGInv"/> to evaluate state
    /// after the run.
    /// </summary>
    public sealed class ScriptRunner
    {
        public const int MaxTicks = 20000;

        public IngameScript.Program Program { get; private set; }
        public FakeGts Gts { get; }
        public IMyProgrammableBlock Me { get; }
        public List<string> EchoMessages { get; } = new List<string>();

        /// <summary>ticks executed (Main calls) during the last Run.</summary>
        public int TicksUsed { get; private set; }

        ScriptRunner(FakeGts gts, IMyProgrammableBlock me)
        {
            Gts = gts;
            Me = me;
        }

        public static ScriptRunner Create(FakeGts gts, IMyProgrammableBlock me)
        {
            return new ScriptRunner(gts, me);
        }

        public static ScriptRunner Create(FakeGts gts, IMyProgrammableBlock me, IMyGridProgramRuntimeInfo runtime)
        {
            var runner = new ScriptRunner(gts, me);
            runner._runtime = runtime;
            return runner;
        }

        IMyGridProgramRuntimeInfo _runtime;

        /// <summary>Builds the Program instance (Game = gateway build + Main loop wiring).</summary>
        public void Build()
        {
            Program = Gateway.CreateProgram()
                .WithGridTerminalSystem(Gts)
                .WithMe(Me)
                .WithRuntime(_runtime)
                .WithEcho(EchoMessages.Add)
                .Build();
        }

        /// <summary>
        /// Runs Main until gInv.updateCounter &gt;= target (or maxTicks is hit).
        /// Returns true if the counter was reached.
        /// </summary>
        public bool RunUntilUpdateCounter(int target, int maxTicks = MaxTicks)
        {
            Build();

            TicksUsed = 0;
            while (TicksUsed < maxTicks)
            {
                Program.Main("", UpdateType.Update1);
                TicksUsed++;
                var inv = GetGInv();
                if (inv != null && inv.updateCounter >= target) return true;
            }
            return false;
        }

        /// <summary>Current gInv instance (null until the block loader finishes).</summary>
        public IngameScript.Program.Inventory GetGInv()
        {
            var field = typeof(IngameScript.Program).GetField("gInv",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return field?.GetValue(null) as IngameScript.Program.Inventory;
        }

        public static void ResetStatics()
        {
            IngameScript.Program._ticks = 0;
            IngameScript.Program.tick = -1;
            IngameScript.Program.Inventory.globalManifest.stuff.Clear();
            IngameScript.Program.Inventory.globalManifest.maxVolume = 0;
            IngameScript.Program.Inventory.globalManifest.freeVolume = 0;
            IngameScript.Program.Inventory.globalManifest.typeVolume.Clear();
            IngameScript.Program.Inventory.encounteredTypes.Clear();
            IngameScript.Program.Inventory.nonFractionalMinMarginByCat.Clear();
            IngameScript.Program.Inventory.prAggs.Clear();
            IngameScript.Program.Inventory.BlockInventory.bPriorityList.Clear();
            IngameScript.Program.Inventory.BlockInventory.bIDict.Clear();
            IngameScript.Program.Inventory.BlockInventory.idl = 0;
            IngameScript.Program.APIWC = null;
            IngameScript.Program.resourceLoader = null;
            // the no-op debug seam: DEBUGGING off + a fresh no-op diag per test
            IngameScript.Program.DEBUGGING = false;
            IngameScript.Program.diag = new IngameScript.Program.DiagBase();
            // the block loader APPENDS to these static lists and never clears
            // them — without a reset, later tests inherit earlier tests' blocks
            // (a stale empty p99 cargo would absorb transfers meant for the
            // current test's receiver).
            var pType = typeof(IngameScript.Program);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            foreach (var name in new[] { "inventoryBlocks", "assemblers", "refineries", "reactors", "controllers", "connectors" })
            {
                var f = pType.GetField(name, flags);
                if (f?.GetValue(null) is System.Collections.IList list) list.Clear();
            }
            // remaining singleton statics: gInv/gProgram, managers, LCDs, Logger
            foreach (var name in new[] { "gInv", "gProgram", "gAssemblerMgr", "gRefineryMgr", "gAutocraft", "gReactorMgr",
                "conduit", "consoleLog", "statusLog", "profileLog", "cargodbg", "autocraftingLCD" })
            {
                var f = pType.GetField(name, flags);
                if (f != null && !f.FieldType.IsValueType) f.SetValue(null, null);
            }
            // discovery + learning STATIC state (held on the nested classes, not
            // the manager singletons): a discovery left in-flight by an earlier
            // test would make update() enter the discovery branch and skip the
            // once-per-second scan; leftover registry entries would change what
            // the queue walk / discovery scan see. Clear them for hermetic tests.
            var asmDiscType = pType.GetNestedType("AsmDiscover", BindingFlags.NonPublic);
            if (asmDiscType != null)
            {
                asmDiscType.GetField("discAssembler", flags)?.SetValue(null, null);
                asmDiscType.GetField("inBaseline", flags)?.SetValue(null, null);
            }
            var refDiscType = pType.GetNestedType("RefDiscover", BindingFlags.NonPublic);
            if (refDiscType != null)
            {
                refDiscType.GetField("discRefinery", flags)?.SetValue(null, null);
                refDiscType.GetField("discLearner", flags)?.SetValue(null, null);
            }
            var refLearnType = pType.GetNestedType("RefLearn", BindingFlags.NonPublic);
            if (refLearnType != null)
            {
                refLearnType.GetField("learned", flags)?.SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>());
                refLearnType.GetField("consumedTotal", flags)?.SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, MyFixedPoint>>());
                refLearnType.GetField("producedTotal", flags)?.SetValue(null, new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>());
            }
            var asmLearnType = pType.GetNestedType("AsmLearn", BindingFlags.NonPublic);
            if (asmLearnType != null)
            {
                asmLearnType.GetField("known", flags)?.SetValue(null, new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>());
            }
            var autocraftType = pType.GetNestedType("Autocraft", BindingFlags.NonPublic);
            if (autocraftType != null)
            {
                autocraftType.GetField("blueprints", flags)?.SetValue(null, new Dictionary<MyDefinitionId, MyDefinitionId>());
            }
            var loggerType = typeof(IngameScript.Program).GetNestedType("Logger", System.Reflection.BindingFlags.NonPublic)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => SafeTypes(a))
                    .FirstOrDefault(t => t.FullName != null && t.FullName.EndsWith("IngameScript.Program+Logger"));
            if (loggerType != null)
            {
                foreach (var f in loggerType.GetFields(flags))
                {
                    if (f.FieldType == typeof(System.Collections.Generic.List<string>) && f.GetValue(null) is System.Collections.IList l) l.Clear();
                    if (f.FieldType == typeof(bool)) f.SetValue(null, false);
                    if (f.FieldType == typeof(System.Text.StringBuilder)) f.SetValue(null, new System.Text.StringBuilder());
                }
            }
        }

        static IEnumerable<Type> SafeTypes(System.Reflection.Assembly a)
        {
            try { return a.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }
    }
}
