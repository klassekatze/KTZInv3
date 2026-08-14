using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
	public partial class Program : MyGridProgram
	{
		// Standalone status text generator. Moved out of Inventory: this function
		// only *retrieves* state - the managers own their cached counts
		// (AssemblerMgr.asmWorking/Stalled/Idle, RefineryMgr.refWorking/Idle,
		// ReactorMgr.fuelByType) and Inventory exposes the fields it reads.
		// Runs every 5 ticks (12x/sec) like the old genstatus did, and only
		// writes the LCD when the text actually changed.
		//
		// NOTE: text is built with StringBuilder.Append chains, NOT the old bapp
		// helper: bapp was a script-defined method with a foreach over a params
		// array, so every call got the rewriter's injected wrap (EnterMethod/
		// CountInstructions/ExitMethod) plus per-arg CountInstructions inside
		// the loop. An Append chain is straight-line method calls on a game API
		// type - zero injected instrumentation.
		void genStatus()
		{
			{ var _ = DEBUGGING ? diag.Enter(DbgLabel.StatusGen) : false; }
			var inv = gInv;
			if (tick % 5 == 0 && inv != null)
			{
				if (inv.errd && tick - inv.rerrtick > 10 * 60)
				{
					inv.errd = false;
					inv.errors.Clear();
				}
				StringBuilder status = new StringBuilder();
				var lbl = inv.statlbl[(int)inv.cstat];
				if (inv.cstat >= Inventory.STATUS.MANIFESTS && inv.cstat != Inventory.STATUS.IDLE)
				{
					status.Append("Working ").Append(inv.nextC + 1).Append("/").Append(inv.containers.Count).Append("\n");
					if (inv.nextC < inv.containers.Count) status.Append(inv.containers[inv.nextC].CustomName).Append("\n");
				}
				status.Append(lbl).Append("\n");
				status.Append(Inventory.transfer_count).Append(" xfer ops this runtime\n\n");

				// assembler manager state: unmanaged if no KTZ Autocrafting LCD
				// (the manager update is LCD-gated), otherwise cached counts.
				if (autocraftingLCD == null)
				{
					status.Append("Assemblers unmanaged, no KTZ Autocrafting LCD\n");
				}
				else
				{
					status.Append("Assemblers: ").Append(gAssemblerMgr.asmWorking).Append(" working, ")
						.Append(gAssemblerMgr.asmStalled).Append(" stalled, ")
						.Append(gAssemblerMgr.asmIdle).Append(" idle\n");
				}

				// refineries and reactor fuel are the immediately relevant ops
				// state; the asm/disasm counters below are more esoteric debug
				status.Append("Refineries: ").Append(gRefineryMgr.refWorking).Append(" working, ")
					.Append(gRefineryMgr.refIdle).Append(" idle\n");

				// per-refinery: what each is refining and why - queue-derived
				// priority ("for assembler queue") vs the static fallback
				// ("by fixed priority order"). Refineries mid-discovery show
				// a "Learning <ore>..." line instead.
				string refMode = gRefineryMgr.queuePriorityActive ? "for assembler queue" : "by fixed priority order";
				for (int i = 0; i < Program.refineries.Count && i < gRefineryMgr.refOre.Count; i++)
				{
					var ore = gRefineryMgr.refOre[i];
					if (ore == default(MyItemType)) continue;
					status.Append("Refining ").Append(ore.SubtypeId).Append(" ").Append(refMode).Append("\n");
				}
				// flag the case where the assemblers want things but none of
				// their queued blueprints are known: the queue-derived
				// priority is empty and the refineries are on the fixed
				// order even though the assemblers are waiting
				if (!gRefineryMgr.queuePriorityActive && RefineryMgr.assemblerQueuesAllUnknown())
				{
					status.Append("(Assembler all unknown recipes)\n");
				}
				// discovery in progress: one line per discovering block
				var learnRef = RefDiscover.learningStatus();
				if (learnRef != "") status.Append(learnRef).Append("\n");
				var learnAsm = AsmDiscover.learningStatus();
				if (learnAsm != "") status.Append(learnAsm).Append("\n");

				// reactor fuel, per type, with /quota when a nonzero autocraft
				// quota exists for that subtype
				foreach (var kvp in gReactorMgr.fuelByType)
				{
					status.Append("Fuel: ").Append(kvp.Key.SubtypeId).Append(" ")
						.Append(((double)kvp.Value).ToString("0.0"));
					int quota = 0;
					if (Autocraft.quotas.TryGetValue(kvp.Key.SubtypeId, out quota) && quota != 0)
					{
						status.Append("/").Append(quota);
					}
					status.Append("\n");
				}

				status.Append("updateCountsAsmDisasmChange: ").Append(gAssemblerMgr.updateCountsAsmDisasmChange).Append("\n");
				status.Append("should_asm: ").Append(gAssemblerMgr.should_asm).Append("\n");
				if (gAssemblerMgr.asm_rsn != "") status.Append(gAssemblerMgr.asm_rsn).Append("\n");
				status.Append("should_disasm: ").Append(gAssemblerMgr.should_disasm).Append("\n");
				if (gAssemblerMgr.disasm_rsn != "") status.Append(gAssemblerMgr.disasm_rsn).Append("\n");

				foreach (var l in inv.errors) status.Append(l).Append("\n");
				var s = status.ToString();
				if (s != inv.lastStatus)
				{
					inv.lastStatus = s;
					if (statusLog != null) statusLog.WriteText(s);
				}
			}
			{ var _ = DEBUGGING ? diag.Exit(DbgLabel.StatusGen) : false; }
		}
	}
}
