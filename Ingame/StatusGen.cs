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
					bapp(status, "Working ", inv.nextC + 1, "/", inv.containers.Count, "\n");
					if (inv.nextC < inv.containers.Count) bapp(status, inv.containers[inv.nextC].CustomName, "\n");
				}
				bapp(status, lbl, "\n");
				bapp(status, Inventory.transfer_count + " xfer ops this runtime\n\n");

				// assembler manager state: unmanaged if no KTZ Autocrafting LCD
				// (the manager update is LCD-gated), otherwise cached counts.
				if (autocraftingLCD == null)
				{
					bapp(status, "Assemblers unmanaged, no KTZ Autocrafting LCD\n");
				}
				else
				{
					bapp(status, "Assemblers: ", gAssemblerMgr.asmWorking, " working, ", gAssemblerMgr.asmStalled, " stalled, ", gAssemblerMgr.asmIdle, " idle\n");
				}
				bapp(status, "updateCountsAsmDisasmChange: " + gAssemblerMgr.updateCountsAsmDisasmChange + "\n");
				bapp(status, "should_asm: " + gAssemblerMgr.should_asm + "\n");
				if (gAssemblerMgr.asm_rsn != "") bapp(status, gAssemblerMgr.asm_rsn + "\n");
				bapp(status, "should_disasm: " + gAssemblerMgr.should_disasm + "\n");
				if (gAssemblerMgr.disasm_rsn != "") bapp(status, gAssemblerMgr.disasm_rsn + "\n");

				// refineries: working state based (cached in the manager)
				bapp(status, "Refineries: ", gRefineryMgr.refWorking, " working, ", gRefineryMgr.refIdle, " idle\n");

				// reactor fuel, per type, with /quota when a nonzero autocraft
				// quota exists for that subtype
				foreach (var kvp in gReactorMgr.fuelByType)
				{
					bapp(status, "Fuel: ", kvp.Key.SubtypeId, " ", kvp.Value);
					int quota = 0;
					if (Autocraft.quotas.TryGetValue(kvp.Key.SubtypeId, out quota) && quota != 0)
					{
						bapp(status, "/", quota);
					}
					bapp(status, "\n");
				}

				foreach (var l in inv.errors) bapp(status, l, "\n");
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
