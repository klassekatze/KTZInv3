using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VRage.Library.Compiler;
using VRage.Scripting;

namespace CompilerProbe
{
    /// <summary>
    /// Replicates the Space Engineers in-game script compile pipeline (same Roslyn
    /// 2.9.0 from Bin64, same CSharp6/Release/X64 options, same implicit namespaces,
    /// same whitelist registration) and then runs the emitted assembly under
    /// IlInjector's run block to measure what the injected instrumentation
    /// (EnterMethod / CountInstructions / ExitMethod) actually costs.
    ///
    /// The whitelist is opened exactly like a plugin does (see the pattern):
    /// MyScriptCompiler.Static.Whitelist.OpenBatch() + AllowNamespaceOfTypes(...)
    /// so scripts may call into a probe namespace that uses Stopwatch - which is
    /// NOT legal in real script source, but IS legal here because the probe type
    /// lives in a test assembly, not in the script.
    /// </summary>
    public static class Program
    {
        const string Bin64 = "/home/user/SpaceEngineers/Bin64/";

        static readonly string[] GameRefs = {
            "Sandbox.Common.dll", "Sandbox.Game.dll", "SpaceEngineers.Game.dll",
            "VRage.dll", "VRage.Game.dll", "VRage.Library.dll", "VRage.Math.dll", "VRage.Scripting.dll",
        };

        static int Main()
        {
            // resolve game assemblies when reflecting over compiled output
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name + ".dll";
                var p = Bin64 + name;
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };

            Console.WriteLine("Roslyn: " + typeof(CSharpCompilation).Assembly.GetName().Version);
            Console.WriteLine("MyScriptCompiler.Static: " + (MyScriptCompiler.Static != null));
            Console.WriteLine();

            SetupCompiler();

            // ---- the probe scripts: EMPTY bodies, only the seam + instrumentation ----
            var scripts = new (string name, string body)[] {
                ("empty_main",
                 "    public void Main(string argument)\n    {\n    }\n"),
                ("empty_main_ternary_false",
                 "    bool DEBUGGING = false;\n" +
                 "    public bool Probe() { return true; }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        { var _ = DEBUGGING ? Probe() : false; }\n" +
                 "    }\n"),
                ("empty_main_ternary_true",
                 "    bool DEBUGGING = true;\n" +
                 "    public bool Probe() { return true; }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        { var _ = DEBUGGING ? Probe() : false; }\n" +
                 "    }\n"),
                ("empty_main_if_false",
                 "    bool DEBUGGING = false;\n" +
                 "    public void Probe() { }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        if (DEBUGGING) Probe();\n" +
                 "    }\n"),
                ("empty_main_one_call",
                 "    public void Probe() { }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        Probe();\n" +
                 "    }\n"),
                ("empty_main_ten_calls",
                 "    public void Probe() { }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        Probe(); Probe(); Probe(); Probe(); Probe();\n" +
                 "        Probe(); Probe(); Probe(); Probe(); Probe();\n" +
                 "    }\n"),
                // ---- labeling question: string literal vs enum vs field as argument ----
                ("arg_string_literal",
                 "    bool DEBUGGING = false;\n" +
                 "    public bool Probe(string label) { return label != null; }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        { var _ = DEBUGGING ? Probe(\"updateT\") : false; }\n" +
                 "    }\n"),
                ("arg_enum",
                 "    bool DEBUGGING = false;\n" +
                 "    public enum Label { UpdateT, Expel, Transfer }\n" +
                 "    public bool Probe(Label label) { return label == Label.UpdateT; }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        { var _ = DEBUGGING ? Probe(Label.UpdateT) : false; }\n" +
                 "    }\n"),
                ("arg_string_field",
                 "    bool DEBUGGING = false;\n" +
                 "    static readonly string UPDATE_T = \"updateT\";\n" +
                 "    public bool Probe(string label) { return label != null; }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        { var _ = DEBUGGING ? Probe(UPDATE_T) : false; }\n" +
                 "    }\n"),
                // ---- exact KTZInv3 seam structure: static field + nested class + virtual ----
                ("ktz_seam",
                 "    public enum DbgLabel { Main, Init, P1 }\n" +
                 "    public class DiagBase\n" +
                 "    {\n" +
                 "        public virtual bool Enter(DbgLabel label) { return true; }\n" +
                 "        public virtual bool Exit(DbgLabel label) { return true; }\n" +
                 "    }\n" +
                 "    public static bool DEBUGGING = false;\n" +
                 "    public static DiagBase diag = new DiagBase();\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        { var _ = DEBUGGING ? diag.Enter(DbgLabel.Main) : false; }\n" +
                 "        { var _ = DEBUGGING ? diag.Exit(DbgLabel.Main) : false; }\n" +
                 "    }\n"),
                // ---- Runtime guard: are CurrentInstructionCount / CurrentCallChainDepth
                // whitelisted, and what does the ternary trip cost? ----
                ("runtime_guard_reads",
                 "    public class ExecutionTripException : System.Exception { }\n" +
                 "    public static bool TripExecution() { throw new ExecutionTripException(); }\n" +
                 "    int MaxInstructionCount;\n" +
                 "    int MaxCallChainDepth;\n" +
                 "    public Program() { MaxInstructionCount = Runtime.MaxInstructionCount * 9 / 10; MaxCallChainDepth = Runtime.MaxCallChainDepth * 9 / 10; }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        { var _ = (Runtime.CurrentInstructionCount > MaxInstructionCount || Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }\n" +
                 "    }\n"),
                ("runtime_guard_reads_x2",
                 "    public class ExecutionTripException : System.Exception { }\n" +
                 "    public static bool TripExecution() { throw new ExecutionTripException(); }\n" +
                 "    int MaxInstructionCount;\n" +
                 "    int MaxCallChainDepth;\n" +
                 "    public Program() { MaxInstructionCount = Runtime.MaxInstructionCount * 9 / 10; MaxCallChainDepth = Runtime.MaxCallChainDepth * 9 / 10; }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        { var _ = (Runtime.CurrentInstructionCount > MaxInstructionCount || Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }\n" +
                 "        { var _ = (Runtime.CurrentInstructionCount > MaxInstructionCount || Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }\n" +
                 "    }\n"),
                // ---- helper-method form (what the fix must NOT do): the call site
                // gets the full injected method wrap (EnterMethod/CountInstructions/
                // ExitMethod) on EVERY guard evaluation - exactly the mandatory
                // instrumentation the inline ternary avoids ----
                ("runtime_guard_methodcall",
                 "    public class ExecutionTripException : System.Exception { }\n" +
                 "    public static bool TripExecution() { throw new ExecutionTripException(); }\n" +
                 "    int MaxInstructionCount;\n" +
                 "    int MaxCallChainDepth;\n" +
                 "    public Program() { MaxInstructionCount = Runtime.MaxInstructionCount * 9 / 10; MaxCallChainDepth = Runtime.MaxCallChainDepth * 9 / 10; }\n" +
                 "    public void TripGuard()\n" +
                 "    {\n" +
                 "        { var _ = (Runtime.CurrentInstructionCount > MaxInstructionCount || Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }\n" +
                 "    }\n" +
                 "    public void Main(string argument)\n    {\n" +
                 "        TripGuard();\n" +
                 "    }\n"),
            };

            foreach (var (name, body) in scripts)
            {
                Measure(name, body, trackMemoryUsage: false);
                Measure(name + "_mem", body, trackMemoryUsage: true);
            }
            return 0;
        }

        /// <summary>Replicates the game's compiler init: game references, implicit
        /// namespaces, and whitelist registration for the probe types.</summary>
        static void SetupCompiler()
        {
            var compiler = MyScriptCompiler.Static;

            // reference the game assemblies (the game's Initialize does the same)
            foreach (var g in GameRefs)
            {
                var p = Bin64 + g;
                if (File.Exists(p)) compiler.AddReferencedAssemblies(p);
            }

            // the game runs on .NET Framework where mscorlib/facades resolve
            // implicitly; on .NET Core we must add the shared framework explicitly
            var fxDir = Directory.GetDirectories("/usr/lib/dotnet/shared/Microsoft.NETCore.App").Last();
            foreach (var dll in Directory.GetFiles(fxDir, "*.dll"))
            {
                try { compiler.AddReferencedAssemblies(dll); } catch { }
            }

            // the game's InitIlCompiler passes these as implicit wrapper namespaces
            // (typeof(IMyGridTerminalSystem) etc.); replicate the ingame ones
            compiler.AddImplicitInGameNamespacesFromTypes(
                typeof(Sandbox.ModAPI.Ingame.IMyGridTerminalSystem),
                typeof(VRage.Game.ModAPI.Ingame.IMyEntity),
                typeof(VRage.Game.ModAPI.Ingame.Utilities.MyIni));

            // the probe assembly must be registered before its types can be whitelisted
            compiler.AddReferencedAssemblies(typeof(StopwatchProbe).Assembly.Location);

            // open a whitelist batch like a plugin would: the game's init whitelists
            // the ingame API namespaces; replicate that for the probe scripts plus
            // our own probe namespace (which uses Stopwatch - illegal in real scripts,
            // legal here because the body lives in a test assembly).
            using (var batch = compiler.Whitelist.OpenBatch())
            {
                batch.AllowNamespaceOfTypes(MyWhitelistTarget.Ingame, typeof(string));                       // System
                batch.AllowNamespaceOfTypes(MyWhitelistTarget.Ingame, typeof(System.ComponentModel.INotifyPropertyChanged)); // wrapper aliases
                batch.AllowNamespaceOfTypes(MyWhitelistTarget.Ingame, typeof(System.Text.StringBuilder));   // wrapper using
                batch.AllowNamespaceOfTypes(MyWhitelistTarget.Ingame, typeof(System.Collections.IEnumerable)); // wrapper using
                batch.AllowNamespaceOfTypes(MyWhitelistTarget.Ingame, typeof(Sandbox.ModAPI.Ingame.IMyTerminalBlock));   // MyGridProgram etc.
                batch.AllowNamespaceOfTypes(MyWhitelistTarget.Ingame, typeof(VRage.Game.ModAPI.Ingame.IMyEntity));
                batch.AllowNamespaceOfTypes(MyWhitelistTarget.Ingame, typeof(StopwatchProbe));
            }
        }

        /// <summary>
        /// Compiles the given script body through the REAL game pipeline
        /// (MyScriptCompiler.Static.Compile with MyApiTarget.Ingame - this runs
        /// TypeSafetyAndBlockRewriter + ResourceMonitoringRewriter, the whitelist
        /// analyzer and the blacklist visitor), then measures Main() execution.
        /// </summary>
        static void Measure(string name, string body, bool trackMemoryUsage)
        {
            var compiler = MyScriptCompiler.Static;
            var script = compiler.GetInGameScript(body, "Program", "MyGridProgram");

            var asm = compiler.Compile(MyApiTarget.Ingame, name, new[] { script },
                new List<Message>(), "probe: " + name, enableDebugInformation: false, trackMemoryUsage).Result;
            if (asm == null)
            {
                // surface WHY it failed (whitelist/blacklist diagnostics)
                var msgs = new List<Message>();
                var asm2 = compiler.Compile(MyApiTarget.Ingame, name, new[] { script },
                    msgs, "probe: " + name, enableDebugInformation: false, trackMemoryUsage).Result;
                Console.WriteLine($"[{name}] COMPILE FAILED");
                foreach (var m in msgs.Take(8))
                    Console.WriteLine("    " + m);
                return;
            }

            var main = asm.GetType("Program").GetMethod("Main", BindingFlags.Public | BindingFlags.Instance);
            var il = main.GetMethodBody().GetILAsByteArray();
            int calls = il.Count(b => b == 0x28 || b == 0x6F);
            var nsInjected = TimeIt(asm, main);

            // BASELINE: same source, same Roslyn/options, but WITHOUT the game's
            // rewriters - this is what the code would cost if SE injected nothing.
            var plain = CompilePlain(script);
            var mainPlain = plain.GetType("Program").GetMethod("Main", BindingFlags.Public | BindingFlags.Instance);
            var ilPlain = mainPlain.GetMethodBody().GetILAsByteArray();
            int callsPlain = ilPlain.Count(b => b == 0x28 || b == 0x6F);
            var nsPlain = TimeIt(plain, mainPlain);

            Console.WriteLine($"[{name}] injected: IL={il.Length}B calls={calls} {nsInjected:0.0} ns/call" +
                              $" | baseline: IL={ilPlain.Length}B calls={callsPlain} {nsPlain:0.0} ns/call" +
                              $" | instr cost: {nsInjected - nsPlain:+0.0;-0.0} ns/call");
            Console.WriteLine();
        }

        /// <summary>Times Main() under an IlInjector run block, best of 5 x 200k.
        /// Uses a compiled delegate (no reflection Invoke overhead) for precision.</summary>
        static double TimeIt(Assembly asm, MethodInfo main)
        {
            // mirror the game exactly: GetUninitializedObject, set Runtime BEFORE
            // the ctor runs (Program() reads Runtime.MaxInstructionCount etc.)
            var type = asm.GetType("Program");
            var instance = FormatterServices.GetUninitializedObject(type);
            using var probeBlock = IlInjector.BeginRunBlock(100000000, 1000, false);
            var fake = new FakeRuntimeInfo(probeBlock);
            try
            {
                typeof(Sandbox.ModAPI.Ingame.MyGridProgram).GetProperty("Runtime",
                    BindingFlags.Public | BindingFlags.Instance)?.SetValue(instance, fake);
            }
            catch { }
            // now run the ctor (the game invokes it right after wiring Runtime)
            type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null)?.Invoke(instance, null);

            var dlg = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), instance, main);
            using (IlInjector.BeginRunBlock(100000000, 1000, false))
                for (int i = 0; i < 10000; i++) dlg("");

            const int N = 200000;
            var sw = new Stopwatch();
            long best = long.MaxValue;
            for (int rep = 0; rep < 5; rep++)
            {
                sw.Restart();
                using (IlInjector.BeginRunBlock(100000000, 1000, false))
                    for (int i = 0; i < N; i++) dlg("");
                sw.Stop();
                best = Math.Min(best, sw.ElapsedTicks);
            }
            return best * (1e9 / Stopwatch.Frequency) / N;
        }

        /// <summary>
        /// Compiles with the exact same references/options the game uses
        /// (CSharp6, Release, DLL, X64, same references) but WITHOUT the
        /// InjectResourceMonitoring rewriters - the uninstrumented baseline.
        /// </summary>
        static Assembly CompilePlain(Script script)
        {
            var parseOpts = new CSharpParseOptions(LanguageVersion.CSharp6, DocumentationMode.None);
            var tree = CSharpSyntaxTree.ParseText(script.Code, parseOpts, script.Name, Encoding.UTF8);
            var opts = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, allowUnsafe: false,
                platform: Platform.X64, checkOverflow: false);

            var refs = new List<MetadataReference>();
            var fxDir = Directory.GetDirectories("/usr/lib/dotnet/shared/Microsoft.NETCore.App").Last();
            foreach (var dll in Directory.GetFiles(fxDir, "*.dll"))
            { try { refs.Add(MetadataReference.CreateFromFile(dll)); } catch { } }
            foreach (var g in GameRefs)
            { var p = Bin64 + g; if (File.Exists(p)) refs.Add(MetadataReference.CreateFromFile(p)); }

            var comp = CSharpCompilation.Create(script.Name, new[] { tree }, refs, opts);
            using var ms = new MemoryStream();
            var result = comp.Emit(ms);
            if (!result.Success) throw new Exception("baseline compile failed: " +
                string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3)));
            return Assembly.Load(ms.ToArray());
        }
    }

    /// <summary>
    /// A type in the probe assembly that the whitelist registration makes visible
    /// to in-game scripts. It uses Stopwatch internally - legal here because the
    /// body is compiled by THIS project, not by the in-game compiler.
    /// </summary>
    public static class StopwatchProbe
    {
        static readonly Stopwatch Sw = new Stopwatch();

        public static void Start() => Sw.Restart();
        public static double ElapsedMicros() => Sw.Elapsed.TotalMicroseconds;
    }

    /// <summary>
    /// Mirrors the game's private RuntimeInfo (MyProgrammableBlock) for headless
    /// probes: delegates every counter to the IlInjector handle exactly like the
    /// real implementation (InstructionCount = m_numInstructions etc.).
    /// </summary>
    public sealed class FakeRuntimeInfo : Sandbox.ModAPI.Ingame.IMyGridProgramRuntimeInfo
    {
        readonly IlInjector.ICounterHandle h;
        public FakeRuntimeInfo(IlInjector.ICounterHandle handle) { h = handle; }

        public int MaxInstructionCount => h.MaxInstructionCount;
        public int CurrentInstructionCount => h.InstructionCount;
        public int MaxCallChainDepth => h.MaxMethodCallCount;
        public int CurrentCallChainDepth => h.MethodCallCount;
        public int Depth => h.Depth;
        public long LifetimeTicks => 0;
        public TimeSpan TimeSinceLastRun => TimeSpan.Zero;
        public double LastRunTimeMs => 0;
        public Sandbox.ModAPI.Ingame.UpdateFrequency UpdateFrequency
        {
            get => Sandbox.ModAPI.Ingame.UpdateFrequency.None;
            set { }
        }
    }
}
