using Sandbox.ModAPI.Ingame;
using System;

namespace IngameScript
{
	public partial class Program : MyGridProgram
	{
		/// <summary>
		/// No-op debug seam, verified against SE's actual compile pipeline
		/// (Roslyn 2.9.0 + ResourceMonitoringRewriter/TypeSafetyAndBlockRewriter):
		///
		///     { var _ = DEBUGGING ? diag.Enter(DbgLabel.X) : false; }
		///     ... work ...
		///     { var _ = DEBUGGING ? diag.Exit(DbgLabel.X) : false; }
		///
		/// In-game (DEBUGGING=false): the ternary is a runtime branch on a field,
		/// never taken - one ldfld + brfalse, ~1ns. The call to diag.Enter/Exit is
		/// never emitted-invoked, so the injected method wrap (EnterMethod /
		/// CountInstructions / try-finally ExitMethod) inside the seam method never
		/// executes. Crucially the ternary is an EXPRESSION, so the rewriter's
		/// VisitIfStatement->InjectedBlock does NOT inject CountInstructions into
		/// the branch (an `if (DEBUGGING)` would).
		///
		/// In tests: set DEBUGGING=true and swap `diag` for a subclass compiled by
		/// the test project (no whitelist, Stopwatch allowed). The virtual call
		/// dispatches to the override, whose body contains ZERO injected calls.
		///
		/// Labels are an enum, NOT string literals: in memory-tracking mode
		/// (ENABLE_PROGRAMMABLE_BLOCK_MEMORY_LIMIT) the rewriter hoists any string
		/// literal argument into `IlInjector.AddMemoryUsage(CalculateStringAllocation(...))`
		/// which executes every call regardless of the branch - an enum argument is
		/// an int (ldc.i4), never tracked. Tests reflect the enum for names.
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
		/// the in-game default is a no-op.
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
