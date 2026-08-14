using Sandbox.Definitions;
using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
	public partial class Program : MyGridProgram
	{
		class RefineryMgr
		{
			List<MyItemType> orePriority = new List<MyItemType>();
			// ordering actually in effect: the queue-derived demand list when
			// assemblers have orders queued, else the static orePriority
			List<MyItemType> activePriority = new List<MyItemType>();
			int queuePriorityTick = -1;
			public RefineryMgr()
			{
				foreach(var ore in gProgram.orePriorityOrder)
				{
					try
					{
						var type = Inventory.getType("MyObjectBuilder_Ore", ore);
						orePriority.Add(type);
					}catch(Exception){ }
				}
				activePriority = orePriority;
			}

			// Ore priority derived from the assembler queues: the composition
			// of the LEADING (first) stack of every assembly-mode assembler,
			// mapped through the learned refinery recipes to the ores needed
			// to satisfy it. An assembler cannot start subsequent queue items
			// until the head completes, so only queue[0] counts per
			// assembler; items with unknown composition are skipped.
			// When assembler demand exists it LEADS the ordering; the static
			// orePriorityOrder follows for ores with no current demand (e.g.
			// stone is always worth processing on SDX2).
			void refreshQueuePriority()
			{
				if (queuePriorityTick == tick) return;
				queuePriorityTick = tick;
				var q = computeQueueOrePriority();
				if (q.Count > 0)
				{
					var blend = new List<MyItemType>(q);
					foreach (var ore in orePriority)
						if (!blend.Contains(ore)) blend.Add(ore);
					activePriority = blend;
				}
				else activePriority = orePriority;
			}

			// blueprint -> item (reverse of Autocraft.blueprints); false when
			// the blueprint is not in the registry. NOTE: no sentinel item is
			// used, because Autocraft.nop is itself a real item (SteelPlate).
			static bool itemForBlueprint(MyDefinitionId bp, out MyItemType item)
			{
				foreach (var kvp in Autocraft.blueprints)
				{
					if (kvp.Value == bp)
					{
						item = (MyItemType)kvp.Key;
						return true;
					}
				}
				item = default(MyItemType);
				return false;
			}

			// demand-weighted ore list: how much of each ore the assembler
			// queues are about to consume, highest demand first
			static List<MyItemType> computeQueueOrePriority()
			{
				// 1. ingot demand from the queue heads (Assembly mode only)
				Dictionary<MyItemType, double> ingotDemand = new Dictionary<MyItemType, double>();
				for (int i = 0; i < Program.assemblers.Count; i++)
				{
					var asm = Program.assemblers[i];
					if (asm.Mode != MyAssemblerMode.Assembly) continue;
					if (AsmDiscover.isDiscovering(asm)) continue;
					List<MyProductionItem> queue = new List<MyProductionItem>();
					asm.GetQueue(queue);
					if (queue.Count == 0) continue;
					var head = queue[0]; // only the leading stack can be produced right now
					MyItemType item;
					if (!itemForBlueprint(head.BlueprintId, out item)) continue;
					var comp = AsmLearn.compositionFor(item);
					if (comp.Count == 0) continue; // unknown composition -> skip
					foreach (var ing in comp)
					{
						if (!ing.Key.GetItemInfo().IsIngot) continue; // refineries make ingots, not components
						double d;
						ingotDemand.TryGetValue(ing.Key, out d);
						ingotDemand[ing.Key] = d + (double)head.Amount * (double)ing.Value;
					}
				}
				if (ingotDemand.Count == 0) return new List<MyItemType>();

				// 2. best learned ratio per (ore -> ingot) across all refinery defs
				Dictionary<MyItemType, Dictionary<MyItemType, double>> oreIngot = new Dictionary<MyItemType, Dictionary<MyItemType, double>>();
				foreach (var defKvp in RefLearn.learned)
				{
					foreach (var oreKvp in defKvp.Value)
					{
						Dictionary<MyItemType, double> outs;
						if (!oreIngot.TryGetValue(oreKvp.Key, out outs))
						{
							outs = new Dictionary<MyItemType, double>();
							oreIngot[oreKvp.Key] = outs;
						}
						foreach (var outKvp in oreKvp.Value)
						{
							double r;
							if (!outs.TryGetValue(outKvp.Key, out r) || (double)outKvp.Value > r)
								outs[outKvp.Key] = (double)outKvp.Value; // most efficient known source wins
						}
					}
				}

				// 3. ore demand: how much of each ore satisfies the ingot demand
				Dictionary<MyItemType, double> oreDemand = new Dictionary<MyItemType, double>();
				foreach (var ing in ingotDemand)
				{
					foreach (var oreKvp in oreIngot)
					{
						double ratio;
						if (!oreKvp.Value.TryGetValue(ing.Key, out ratio) || ratio <= 0) continue;
						double d;
						oreDemand.TryGetValue(oreKvp.Key, out d);
						oreDemand[oreKvp.Key] = d + ing.Value / ratio;
					}
				}

				// 4. highest demand first
				return oreDemand.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
			}


			Inventory.InventoryManifest NonRefManifest = new Inventory.InventoryManifest();
			List<MyItemType> availOrePriority = new List<MyItemType>();
			int availOrePriorityNo1Index = -1;
			public void computeFactors()
			{
				refreshQueuePriority();
				//copy the global inventory manifest then subtract refineries from it.
				NonRefManifest = new Inventory.InventoryManifest();
				NonRefManifest.add(Inventory.globalManifest);
				for (int i = 0; i < Program.refineries.Count; i++)
				{
					var r = Program.refineries[i];
					NonRefManifest.sub(Inventory.BlockInventory.getBI(r).manifest);
				}
				//create subset of ore priority list that only has what is available outside a refinery right now
				availOrePriority.Clear();
				foreach (var ore in activePriority)
				{
					MyFixedPoint amt = 0;
					NonRefManifest.stuff.TryGetValue(ore, out amt);
					if (amt > 0)
					{
						availOrePriority.Add(ore);
					}
				}
				if (availOrePriority.Count == 0) return;
				availOrePriorityNo1Index = activePriority.IndexOf(availOrePriority[0]);
			}

			int curUpdate = -1;
			//public Inventory.InventoryManifest NonRefManifest = new Inventory.InventoryManifest();

			bool flipflop = false;

			int refi = 0;

			// cached status counts, refreshed once per second (working state
			// based, per the status display requirement)
			public int refWorking = 0;
			public int refIdle = 0;

			public void update()
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				if (tick % 60 == 0)
				{
					refWorking = refIdle = 0;
					for (int i = 0; i < Program.refineries.Count; i++)
					{
						if (Program.refineries[i].IsProducing) refWorking++;
						else refIdle++;
					}
				}
				if (!gInv.hasUpdatedOnce) return;

				if (gInv.updateCounter != curUpdate)
				{
					refi = 0;
				}
				curUpdate = gInv.updateCounter;

				if (refi < Program.refineries.Count)
				{
					// a refinery being used for recipe discovery is excluded
					// from normal management: RefDiscover flushed and stuffed
					// it, so top-up/expel here would fight the observation
					if (RefDiscover.isDiscovering(Program.refineries[refi]))
					{
						refi++;
						return;
					}

					computeFactors();
					if (availOrePriority.Count == 0) return;

					int i = refi;

					{
						var r = Program.refineries[i];
						var bi = Inventory.BlockInventory.getBI(r);

						MyInventoryItem? item0 = r.InputInventory.GetItemAt(0);

						MyItemType stockType = availOrePriority[0];
						bool should_update = true;
						if (item0.HasValue)
						{


							if (item0.Value.Type == availOrePriority[0])
							{
								should_update = false;
							}
							else//because we ignore refinery content we may be currently proccing an ore more important than is in availOrePriority. in that case, we do not want to remove it
							{
								int idx1 = activePriority.IndexOf(item0.Value.Type);
								if (idx1 <= availOrePriorityNo1Index)
								{
									should_update = false;
									stockType = item0.Value.Type;
								}
							}
							MyFixedPoint avail = 0;
							NonRefManifest.stuff.TryGetValue(item0.Value.Type, out avail);
							if (item0.Value.Amount < 3000 && avail > 0)//we arbitrarily use 3000kg (i think) as the top up point
							{
								should_update = true;
							}
						}
						if (should_update)
						{
							var uconv = r.UseConveyorSystem;

							var amt = (MyFixedPoint)Math.Floor((double)r.InputInventory.MaxVolume / Inventory.getItemInfo(stockType).Volume);


							//Inventory.retrieve(bi, stockType, amt, false, true);
							List<MyInventoryItem> items = new List<MyInventoryItem>();
							r.InputInventory.GetItems(items);
							//MyFixedPoint vol = bi.manifest.maxVolume;
							foreach (var item in items)
							{
								if (item.Type != stockType)
								{
									Inventory.expel(bi, item.Type, item.Amount, true);
									log("RM: expelling " + item.Type.SubtypeId + " from ref " + r.CustomName);
								}//else
								 //{
								 //vol -= item.Amount * item.Type.GetItemInfo().Volume;
								 //}
							}

							log("RM: retrieving " + amt + " " + stockType.TypeId + "/" + stockType.SubtypeId + " to ref " + r.CustomName);


							amt = Inventory.force_retrieve(bi, stockType, amt, false, true);
							log("RM: unfulfilled: " + amt);

						}
					}
					refi++;
				}
			}
		}
	}
}
