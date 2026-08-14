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
		/// into an isolated assembler and queue its disassembly - the output
		/// IS the recipe, ingredient for ingredient, in exact amounts.
		///
		/// Trigger: item has a known autocraft blueprint, we possess at least
		/// one copy of it (in the global manifest), its composition is
		/// unknown (AsmLearn), and an enabled assembler is available.
		///
		/// Isolation mirrors RefDiscover: the assembler is excluded from
		/// normal assembler management (AssemblerMgr skips it), locked
		/// against the sorter (lock survives renames via updateP), its
		/// conveyor system is disabled, it is flushed, one copy of the item
		/// is stuffed into its input, and the disassembly blueprint is queued
		/// with a negative amount. Once the item has been consumed and the
		/// output gained ingredients, the composition is exact (output delta
		/// for exactly one disassembled unit), saved to the registry, and
		/// written to CustomData.
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
			// output snapshot taken right after the flush, so the composition
			// is the output DELTA (leftovers can't pollute it)
			static List<MyInventoryItem> outBaseline = null;
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

			public void update()
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				if (!ASM_DISCOVER) return;
				if (!gInv.hasUpdatedOnce) return;

				if (discAssembler != null)
				{
					// disassembly completes when the item has been fully
					// consumed and the output gained ingredients
					var bi = Inventory.BlockInventory.getBI(discAssembler);
					MyFixedPoint have = 0;
					if (bi.manifest != null)
						bi.manifest.stuff.TryGetValue((MyItemType)discItem, out have);

					if (have == 0 && outputGained())
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
				startNextDiscovery();
			}

			// whether the assembler's output contains anything beyond the
			// post-flush baseline
			static bool outputGained()
			{
				List<MyInventoryItem> now = new List<MyInventoryItem>();
				discAssembler.OutputInventory.GetItems(now);
				if (outBaseline == null) return now.Count > 0;
				// per-type comparison against the baseline
				foreach (var n in now)
				{
					MyFixedPoint b = 0;
					foreach (var o in outBaseline)
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
				outBaseline = null;

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
				// disassembly mode: the game's manifest view then tracks the
				// input (the item being consumed) instead of the output
				a.Mode = MyAssemblerMode.Disassembly;

				// flush input and output so the observation starts clean
				var items = new List<MyInventoryItem>();
				a.InputInventory.GetItems(items);
				foreach (var it in items) Inventory.expel(bi, it.Type, it.Amount, true);
				items.Clear();
				a.OutputInventory.GetItems(items);
				foreach (var it in items) Inventory.expel(bi, it.Type, it.Amount, false);

				// stuff exactly one copy of the item into the input
				var left = Inventory.force_retrieve(bi, (MyItemType)item, (MyFixedPoint)1, false, true);
				if (left > 0)
				{
					log("AsmDiscover: could not retrieve a copy of " + item.SubtypeId + " for " + a.CustomName, LT.LOG_N);
					release(false);
					return;
				}

				// snapshot what we're about to clear so release() can restore
				// it: the user may have queued a bunch of jobs (e.g. teaching
				// several blueprints) and expects them to survive
				// (already captured above, before the mode was flipped)

				// queue the disassembly (negative amount = disassemble)
				a.ClearQueue();
				a.AddQueueItem(bp, (MyFixedPoint)(-1));

				// snapshot the (now flushed) output as the composition baseline
				outBaseline = new List<MyInventoryItem>();
				a.OutputInventory.GetItems(outBaseline);

				log("AsmDiscover: disassembling 1x " + item.SubtypeId + " in " + a.CustomName, LT.LOG_N);
			}

			void release(bool learned)
			{
				var a = discAssembler;
				var item = discItem;
				var baseline = outBaseline; // composition needs it, so capture first
				var modeBackup = discModeBackup;
				var queueBackup = discQueueBackup;
				discAssembler = null;
				outBaseline = null;
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

				if (learned)
				{
					// composition = output delta (exactly one unit was
					// disassembled, so the delta IS the per-unit recipe)
					var comp = new Dictionary<MyItemType, MyFixedPoint>();
					List<MyInventoryItem> now = new List<MyInventoryItem>();
					a.OutputInventory.GetItems(now);
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
