using Sandbox.ModAPI.Ingame;
using System;

namespace IngameScript
{
	public partial class Program : MyGridProgram
	{
		/// <summary>
		/// No-op debug seam. Verified empirically against SE's actual compile
		/// pipeline (Roslyn 2.9.0 from Bin64 + TypeSafetyAndBlockRewriter +
		/// ResourceMonitoringRewriter + WhitelistDiagnosticAnalyzer, driven via
		/// MyScriptCompiler.Static and CompilerProbe in this repo).
		///
		/// HOW TO USE — call sites look EXACTLY like this (no other shape):
		///
		///     { var _ = DEBUGGING ? diag.Enter(DbgLabel.Invu) : false; }
		///     ...work being timed...
		///     { var _ = DEBUGGING ? diag.Exit(DbgLabel.Invu) : false; }
		///
		/// In-game (DEBUGGING=false): zero side effects, ~1ns per call site.
		/// In tests: set DEBUGGING=true and swap `diag` for a subclass compiled
		/// by the test project (Stopwatch etc. legal there), run Main(), read the
		/// per-label timings out of the override.
		///
		/// WHY THIS EXACT SHAPE — DO NOT "SIMPLIFY". Every constraint below was
		/// measured, not guessed:
		///
		/// 1. DEBUGGING must be a NON-const field. If it were `const bool
		///    DEBUGGING = false`, Roslyn constant-folds the conditional at
		///    compile time and the ENTIRE line vanishes from the IL — which
		///    sounds nice but (a) means the test framework can never flip it,
		///    and (b) is the one case where the whitelist analyzer still checks
		///    the dead branch (it walks the whole tree) but the call is gone.
		///    As a field, the IL keeps `ldfld DEBUGGING; brfalse.s skip` — a
		///    runtime branch that is simply never taken in-game.
		///
		/// 2. It must be a TERNARY EXPRESSION, not `if (DEBUGGING) diag.Enter(...)`.
		///    ResourceMonitoringRewriter.VisitIfStatement wraps if-bodies in
		///    InjectedBlock, which prepends `IlInjector.CountInstructions()`
		///    INSIDE the branch body. A ternary is an expression, not a
		///    statement body, so the rewriter injects NOTHING into the branch.
		///    Measured: `if` variant IL=38B with an extra injected call vs
		///    ternary IL=36B without. (Both are free when the branch is not
		///    taken, but the if injects one more call instruction into the
		///    method and the timing is +8.1 vs +7.1 ns/call.)
		///
		/// 3. The discarded result must be `var _ =` inside a BLOCK `{ }`.
		///    The ternary produces a bool value; a bare expression statement is
		///    illegal, and the block scopes the `_` variable so repeated call
		///    sites in the same method don't collide. The assignment is elided
		///    by Release optimization (verified: no stloc in the IL).
		///
		/// 4. Labels are an ENUM, not string literals. In memory-tracking mode
		///    (ENABLE_PROGRAMMABLE_BLOCK_MEMORY_LIMIT — off by default but a
		///    server/plugin can enable it), ResourceMonitoringRewriter hoists any
		///    string-literal argument into
		///    `IlInjector.AddMemoryUsage(IlInjector.CalculateStringAllocation(temp))`
		///    as a SEPARATE statement BEFORE the call site — it executes every
		///    call even when DEBUGGING=false, forever. An enum argument is an
		///    int (ldc.i4), never tracked. Measured: string-literal arg gets
		///    +2 injected calls in mem mode; enum stays clean in both modes.
		///    The test framework reflects the enum for human-readable names.
		///
		/// 5. The seam methods return bool. The ternary needs both branches to
		///    type-check: `DEBUGGING ? diag.Enter(x) : false` requires
		///    Enter(x) to be bool-compatible with the false literal. Returning
		///    true is also the natural "no-op succeeded" value.
		///
		/// 6. No helper method like `void Dbg(DbgLabel l)` — the ternary MUST
		///    be inline at the call site. Any method called from the script gets
		///    the injected EnterMethod/CountInstructions/try-finally/ExitMethod
		///    wrap (measured ~9.7 ns per called method). A helper would be
		///    CALLED every tick, so its wrap executes every tick even when
		///    DEBUGGING=false. The inline ternary's wrap never executes because
		///    the call is never dispatched.
		///
		/// 7. The seam class must be SCRIPT-DEFINED (IsInSource). The whitelist
		///    analyzer only flags symbols NOT in source; a call into a script
		///    class is legal even in a dead branch. A game API call in the
		///    branch would be PROHIBITED_MEMBER at compile time — dead code is
		///    NOT exempt from the whitelist. The override lives in the test
		///    assembly, so its body (Stopwatch, reflection, ...) is never seen
		///    by the game's compiler.
		///
		/// 8. static fields match the existing Profiler pattern (all call sites
		///    are inside nested classes that access them unqualified). The test
		///    framework sets Program.DEBUGGING / Program.diag directly and
		///    ResetStatics() restores them per test.
		///
		/// Measured total in-game cost per call site: +7.5 ns (one field load +
		/// one never-taken branch), identical in both memory modes. The seam
		/// method's own injected wrap never executes because the call is never
		/// dispatched.
		/// </summary>
		public enum DbgLabel
		{
			Init,
			Main,
			Aclcd1,
			Aclcd2,
			Stat,
			Cdbg,
			Invu,
			Bpl,
			AsmShuf,
			AsmBal,
			P1,
			P2,
			P3,
		}

		/// <summary>
		/// Script-defined seam base. Must be script-defined (not a game type):
		/// the whitelist analyzer only flags symbols not IsInSource(), so a call
		/// into this class is legal in dead branches. Empty virtual methods =
		/// the in-game default is a no-op. The test framework subclasses this
		/// and overrides Enter/Exit to record timings.
		/// </summary>
		public class DiagBase
		{
			public virtual bool Enter(DbgLabel label) { return true; }
			public virtual bool Exit(DbgLabel label) { return true; }
		}

		/// <summary>NOT const: a const would be folded away at compile time and
		/// could never be flipped by the test framework. A field keeps the branch
		/// alive in IL (one ldfld + brfalse per site, never taken in-game).</summary>
		public static bool DEBUGGING = false;

		/// <summary>The seam instance. The test framework replaces this with a
		/// subclass override before running Main().</summary>
		public static DiagBase diag = new DiagBase();
	}
}
