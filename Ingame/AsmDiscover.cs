using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
	public partial class Program : MyGridProgram
	{
		/// <summary>
		/// Assembler recipe discovery by disassembly. We may know an autocraft
		/// blueprint (item -> blueprint) without knowing what the item is made
		/// of. Refinery conversions have to be inferred from deltas, but an
		/// assembler recipe is exact and enactable: put one copy of the item
		/// into an isolated assembler and queue its disassembly - the
		/// ingredients that come out ARE the recipe, in exact amounts.
		///
		/// Inventory layout mirrors the game (decompiled MyAssembler): the
		/// assembled item being disassembled lives in the OUTPUT inventory
		/// (UpdateDisassembleMode pulls it there), and the ingredients land in
		/// the INPUT inventory (FinishDisassembling removes the results from
		/// output and adds the prerequisites to input). The queue amount is
		/// POSITIVE - direction is the Mode (DisassembleEnabled), exactly like
		/// the player UI and AssemblerMgr (which negates its internal orders
		/// sign convention back to positive before AddQueueItem).
		///
		/// Trigger: item has a known autocraft blueprint, we possess at least
		/// one copy of it (in the global manifest), its composition is
		/// unknown (AsmLearn), and an enabled assembler that can use the
		/// blueprint is available.
		///
		/// Isolation mirrors RefDiscover: the assembler is excluded from
		/// normal assembler management (AssemblerMgr skips it), locked
		/// against the sorter (lock survives renames via updateP), its
		/// conveyor system is disabled, it is flushed, one copy of the item
		/// is stuffed into its OUTPUT inventory, and the disassembly
		/// blueprint is queued with a POSITIVE amount while the assembler is
		/// in Disassembly mode. Once the item has been consumed from the
		/// output and the input gained ingredients, the composition is exact
		/// (input delta for exactly one disassembled unit), saved to the
		/// registry, and written to CustomData.
		/// </summary>
		class AsmDiscover
		{
			// safety net: if the disassembly hasn't completed within this
			// long, release the assembler instead of holding it forever
			const int DISCOVER_TIMEOUT_TICKS = 60 * 60 * 10;

			static IMyAssembler discAssembler = null;
			static MyDefinitionId discItem; // item def (blueprints key)
			static MyDefinitionId discBlueprint;
			static int discStartTick = -1;
			// input snapshot taken right after the flush, so the composition
			// is the input DELTA (leftovers can't pollute it)
			static List<MyInventoryItem> inBaseline = null;
			// what we cleared from the assembler, so release() can put it
			// back: the user may have queued a bunch of jobs (e.g. teaching
			// several blueprints) and expects them to survive the discovery
			static MyAssemblerMode discModeBackup;
			static List<MyProductionItem> discQueueBackup = null;

			// whether the given block is the assembler currently being used
			// for discovery (checked by the sorter's updateP so the lock
			// survives renames)
			static public bool isDiscovering(IMyCubeBlock b)
			{
				return b != null && b == discAssembler;
			}

			// status display: "Learning <item>..." while a discovery run is
			// in progress, empty string otherwise
			static public string learningStatus()
			{
				if (discAssembler == null) return "";
				return "Learning " + discItem.SubtypeId + "...";
			}

			public void update()
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				if (!ASM_DISCOVER) return;
				if (!gInv.hasUpdatedOnce) return;

				if (discAssembler != null)
				{
					// disassembly completes when the item has been consumed
					// from the OUTPUT inventory and the INPUT inventory
					// gained ingredients (the game's FinishDisassembling
					// removes the results from output, adds prerequisites to
					// input). Note: in Disassembly mode the BlockInventory
					// manifest view is input-only, so we read the item's
					// presence directly from the output inventory.
					bool itemGone = true;
					List<MyInventoryItem> outNow = new List<MyInventoryItem>();
					discAssembler.OutputInventory.GetItems(outNow);
					foreach (var o in outNow)
					{
						if (o.Type == (MyItemType)discItem && o.Amount > 0) { itemGone = false; break; }
					}

					if (itemGone && inputGained())
					{
						release(true);
					}
					else if (tick - discStartTick > DISCOVER_TIMEOUT_TICKS)
					{
						log("AsmDiscover: giving up on " + discItem.SubtypeId + " in " + discAssembler.CustomName + " (no disassembly within timeout)", LT.LOG_N);
						release(false);
					}
					return;
				}

				// scan for a candidate at most once per second
				if (tick % 60 != 0) return;
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.AsmScan) : false; }
				startNextDiscovery();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.AsmScan) : false; }
			}

			// whether the assembler's INPUT contains anything beyond the
			// post-flush baseline (the ingredients produced by disassembly)
			static bool inputGained()
			{
				List<MyInventoryItem> now = new List<MyInventoryItem>();
				discAssembler.InputInventory.GetItems(now);
				if (inBaseline == null) return now.Count > 0;
				// per-type comparison against the baseline
				foreach (var n in now)
				{
					MyFixedPoint b = 0;
					foreach (var o in inBaseline)
					{
						if (o.Type == n.Type) { b = o.Amount; break; }
					}
					if (n.Amount > b) return true;
				}
				return false;
			}

			void startNextDiscovery()
			{
				foreach (var kvp in Autocraft.blueprints)
				{
					var item = kvp.Key;
					var bp = kvp.Value;
					if (AsmLearn.knowsRecipe((MyItemType)item)) continue;
					MyFixedPoint amt = 0;
					Inventory.globalManifest.stuff.TryGetValue((MyItemType)item, out amt);
					if (amt < (MyFixedPoint)1) continue;
					foreach (var a in Program.assemblers)
					{
						if (!a.Enabled) continue;
						if (isDiscovering(a)) continue;
						if (!a.CanUseBlueprint(bp)) continue;
						start(a, item, bp);
						return;
					}
				}
			}

			void start(IMyAssembler a, MyDefinitionId item, MyDefinitionId bp)
			{
				discAssembler = a;
				discItem = item;
				discBlueprint = bp;
				discStartTick = tick;
				inBaseline = null;

				var bi = Inventory.BlockInventory.getBI(a);

				// snapshot what we're about to change so release() can put it
				// back: the user may have queued a bunch of jobs (e.g. teaching
				// several blueprints) and expects them to survive the discovery
				discModeBackup = a.Mode;
				discQueueBackup = new List<MyProductionItem>();
				a.GetQueue(discQueueBackup);

				// excluded from normal assembler management (AssemblerMgr
				// skips discovering assemblers) + locked against the sorter
				// (updateP also re-applies the lock while isDiscovering)
				bi.locked = true;
				// the assembler must not pull/push items of its own volition
				// during the observation; the script's own transfers still work
				a.UseConveyorSystem = false;
				// disassembly mode: the game then removes the queued item
				// from the output and produces its ingredients into the input
				a.Mode = MyAssemblerMode.Disassembly;

				// flush input and output so the observation starts clean
				var items = new List<MyInventoryItem>();
				a.InputInventory.GetItems(items);
				foreach (var it in items) Inventory.expel(bi, it.Type, it.Amount, true);
				items.Clear();
				a.OutputInventory.GetItems(items);
				foreach (var it in items) Inventory.expel(bi, it.Type, it.Amount, false);

				// stuff exactly one copy of the item into the OUTPUT
				// inventory: that is where the game looks for the item being
				// disassembled (decompiled UpdateDisassembleMode pulls it
				// there). UseConveyorSystem is off, so we place it ourselves.
				// Done with the low-level raw transfer, not the BlockInventory
				// boolean overload: in Disassembly mode the inv system's
				// inventory view treats the INPUT as the product side
				// (getSortedInventories(false) -> sortedInventoriesNoOutput =
				// input), so the booleans would route the transfer into the
				// wrong inventory. expel (flush) above stays in the inv
				// system; only the targeted stuffing goes direct.
				MyFixedPoint left = (MyFixedPoint)1;
				foreach (var ibi in Inventory.BlockInventory.bPriorityList)
				{
					MyFixedPoint available = 0;
					if (ibi.manifest != null)
						ibi.manifest.stuff.TryGetValue((MyItemType)item, out available);
					if (available <= 0) continue;
					foreach (var srcInv in ibi.getSortedInventories(true))
					{
						left = Inventory.transfer_item(srcInv, a.OutputInventory, (MyItemType)item, left);
						if (left <= (MyFixedPoint)0.001d) break;
					}
					if (left <= (MyFixedPoint)0.001d) break;
				}
				if (left > (MyFixedPoint)0.001d)
				{
					log("AsmDiscover: could not retrieve a copy of " + item.SubtypeId + " for " + a.CustomName, LT.LOG_N);
					release(false);
					return;
				}

				// queue the disassembly: POSITIVE amount (the game's queue
				// count is positive; the direction is the Mode, exactly like
				// the player UI and AssemblerMgr)
				a.ClearQueue();
				a.AddQueueItem(bp, (MyFixedPoint)1);

				// snapshot the (now flushed) INPUT as the composition baseline
				inBaseline = new List<MyInventoryItem>();
				a.InputInventory.GetItems(inBaseline);

				log("AsmDiscover: disassembling 1x " + item.SubtypeId + " in " + a.CustomName, LT.LOG_N);
			}

			void release(bool learned)
			{
				var a = discAssembler;
				var item = discItem;
				var baseline = inBaseline; // composition needs it, so capture first
				var modeBackup = discModeBackup;
				var queueBackup = discQueueBackup;
				discAssembler = null;
				inBaseline = null;
				discQueueBackup = null;

				if (a == null) return;

				a.UseConveyorSystem = true;
				var bi = Inventory.BlockInventory.getBI(a);
				bi.locked = false;

				// put back what we cleared: the user's queue (jobs are
				// re-added in their original order) and the assembler's mode.
				// ClearQueue first so any residual discovery job can't linger
				a.ClearQueue();
				a.Mode = modeBackup;
				if (queueBackup != null)
				{
					foreach (var qi in queueBackup)
					{
						a.AddQueueItem(qi.BlueprintId, qi.Amount);
					}
				}
				// the discovery consumed one copy of the item: queue the
				// assembly of one replacement so the stock isn't silently
				// depleted. Only in Assembly mode - in Disassembly mode the
				// user is breaking items down, not building them up.
				if (learned && modeBackup == MyAssemblerMode.Assembly)
				{
					a.AddQueueItem(discBlueprint, (MyFixedPoint)1);
				}

				if (learned)
				{
					// composition = input delta (exactly one unit was
					// disassembled, so the delta IS the per-unit recipe)
					var comp = new Dictionary<MyItemType, MyFixedPoint>();
					List<MyInventoryItem> now = new List<MyInventoryItem>();
					a.InputInventory.GetItems(now);
					foreach (var n in now)
					{
						MyFixedPoint b = 0;
						if (baseline != null)
						{
							foreach (var o in baseline)
							{
								if (o.Type == n.Type) { b = o.Amount; break; }
							}
						}
						if (n.Amount > b) comp[n.Type] = n.Amount - b;
					}
					AsmLearn.record((MyItemType)item, comp);
					log("AsmDiscover: learned " + item.SubtypeId + " = " + string.Join(", ", comp.Select(kvp => kvp.Key.SubtypeId + " x" + ((double)kvp.Value).ToString("0.###"))), LT.LOG_N);
				}
				// save the pattern to the registry and write everything
				// (assembler BPs + refinery recipes + compositions) again
				Autocraft.writeCD();
			}
		}
	}
}
