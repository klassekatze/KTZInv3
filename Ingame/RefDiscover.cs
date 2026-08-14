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
		/// Refinery recipe discovery. Assembler learning is continuous and
		/// passive because an unknown blueprint can't be enacted; refineries
		/// are different - no hidden knowledge is needed to run one, so an
		/// unknown (refinery type, ore) conversion can be actively discovered:
		/// take an enabled refinery that accepts the ore, exclude it from
		/// normal refinery management, lock it against the sorter, disable its
		/// conveyor system, flush it, stuff it with the unknown ore, and let
		/// RefLearn observe the resulting single-input windows. Once the
		/// pattern is learned the refinery is released (UseConveyors restored,
		/// unlocked, back under RefineryMgr) and the registry is written to
		/// CustomData alongside the assembler blueprints.
		///
		/// Knowledge is keyed by refinery BLOCK DEFINITION: a recipe learned
		/// on a regular refinery does not satisfy a blast forge (e.g. SDX2
		/// gives boron only from the blast forge for stone), so each
		/// (refineryDef, ore) pair is discovered independently.
		/// </summary>
		class RefDiscover
		{
			// how much of an ore must exist on the grid before we spend a
			// refinery on discovering its recipe (matches RefineryMgr's 3000
			// top-up point)
			const int DISCOVER_MIN_AMOUNT = 3000;
			// safety net: if no pattern has been learned within this long,
			// release the refinery instead of holding it locked forever
			const int DISCOVER_TIMEOUT_TICKS = 60 * 60 * 10;

			static IMyRefinery discRefinery = null;
			static MyItemType discOre;
			static int discStartTick = -1;

			// whether the given block is the refinery currently being used
			// for discovery (checked by the sorter's updateP so the lock
			// survives renames)
			static public bool isDiscovering(IMyCubeBlock b)
			{
				return b != null && b == discRefinery;
			}

			public void update()
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				if (!REFINERY_LEARN) return;
				if (!gInv.hasUpdatedOnce) return;

				if (discRefinery != null)
				{
					var refDef = (MyDefinitionId)discRefinery.BlockDefinition;
					if (RefLearn.knowsRecipe(refDef, discOre))
					{
						release(true);
					}
					else if (tick - discStartTick > DISCOVER_TIMEOUT_TICKS)
					{
						log("RefDiscover: giving up on " + discOre.SubtypeId + " in " + discRefinery.CustomName + " (no pattern within timeout)", LT.LOG_N);
						release(false);
					}
					return;
				}

				// scan for a candidate at most once per second
				if (tick % 60 != 0) return;
				startNextDiscovery();
			}

			void startNextDiscovery()
			{
				foreach (var ore in oreCandidates())
				{
					foreach (var r in Program.refineries)
					{
						if (!r.Enabled) continue;
						if (isDiscovering(r)) continue;
						var refDef = (MyDefinitionId)r.BlockDefinition;
						if (RefLearn.knowsRecipe(refDef, ore)) continue;
						if (!acceptsOre(r, ore)) continue;
						start(r, ore);
						return;
					}
				}
			}

			// ores with >= DISCOVER_MIN_AMOUNT in the global manifest,
			// priority-ordered first, then anything else with enough stock
			static List<MyItemType> oreCandidates()
			{
				var res = new List<MyItemType>();
				foreach (var ore in gProgram.orePriorityOrder)
				{
					try
					{
						var t = Inventory.getType("MyObjectBuilder_Ore", ore);
						if (haveEnough(t)) res.Add(t);
					}
					catch (Exception) { }
				}
				foreach (var kvp in Inventory.globalManifest.stuff)
				{
					if (Inventory.getItemInfo(kvp.Key).IsOre && !res.Contains(kvp.Key) && haveEnough(kvp.Key)) res.Add(kvp.Key);
				}
				return res;
			}

			static bool haveEnough(MyItemType ore)
			{
				MyFixedPoint amt = 0;
				Inventory.globalManifest.stuff.TryGetValue(ore, out amt);
				return amt >= (MyFixedPoint)DISCOVER_MIN_AMOUNT;
			}

			static bool acceptsOre(IMyRefinery r, MyItemType ore)
			{
				var accepted = new List<MyItemType>();
				r.InputInventory.GetAcceptedItems(accepted);
				return accepted.Contains(ore);
			}

			void start(IMyRefinery r, MyItemType ore)
			{
				discRefinery = r;
				discOre = ore;
				discStartTick = tick;

				var bi = Inventory.BlockInventory.getBI(r);

				// excluded from normal refinery management + locked against
				// the sorter (updateP also re-applies the lock while
				// isDiscovering, so a rename can't drop it)
				bi.locked = true;
				// the refinery must not move items of its own volition during
				// the observation; the script's own transfers still work
				r.UseConveyorSystem = false;

				// flush input and output so the learner starts from a clean slate
				var items = new List<MyInventoryItem>();
				r.InputInventory.GetItems(items);
				foreach (var it in items) Inventory.expel(bi, it.Type, it.Amount, true);
				items.Clear();
				r.OutputInventory.GetItems(items);
				foreach (var it in items) Inventory.expel(bi, it.Type, it.Amount, false);

				// stuff with the unknown ore (up to capacity, capped at the
				// discovery amount so we don't hoard the whole stock)
				var amt = (MyFixedPoint)Math.Floor((double)r.InputInventory.MaxVolume / Inventory.getItemInfo(ore).Volume);
				if (amt > (MyFixedPoint)DISCOVER_MIN_AMOUNT) amt = (MyFixedPoint)DISCOVER_MIN_AMOUNT;
				var left = Inventory.force_retrieve(bi, ore, amt, false, true);
				log("RefDiscover: discovering " + ore.SubtypeId + " in " + r.CustomName + " (stuffed " + (amt - left) + ")", LT.LOG_N);

				// the learner's baseline must not include the flush: reset its
				// snapshots so the next update takes a fresh baseline
				RefLearn.resetForMachine(r);
			}

			void release(bool learned)
			{
				var r = discRefinery;
				var ore = discOre;
				discRefinery = null;

				if (r == null) return;

				r.UseConveyorSystem = true;
				var bi = Inventory.BlockInventory.getBI(r);
				bi.locked = false;

				if (learned)
				{
					var outs = RefLearn.outputsFor((MyDefinitionId)r.BlockDefinition, ore);
					log("RefDiscover: learned " + ore.SubtypeId + " -> " + string.Join(", ", outs.Select(kvp => kvp.Key.SubtypeId + " x" + ((double)kvp.Value).ToString("0.###"))), LT.LOG_N);
				}
				// save the pattern to the registry and write everything
				// (assembler BPs + refinery recipes) to CustomData again
				Autocraft.writeCD();
			}
		}
	}
}
