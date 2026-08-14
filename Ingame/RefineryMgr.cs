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
			// true when activePriority is the assembler-queue-derived blend
			// (status display: "for assembler queue" vs "by fixed priority
			// order")
			public bool queuePriorityActive = false;
			// what each refinery currently has in its input (per index, kept
			// in sync with Program.refineries; status display reads it)
			public List<MyItemType> refOre = new List<MyItemType>();
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
				queuePriorityActive = q.Count > 0;
				if (q.Count > 0)
				{
					var blend = new List<MyItemType>(q);
					foreach (var ore in orePriority)
						if (!blend.Contains(ore)) blend.Add(ore);
					activePriority = blend;
				}
				else activePriority = orePriority;
			}

			// true when there ARE assembler queues (Assembly mode) but NONE
			// of their blueprints are known to the registry. In that case
			// the queue-derived priority is empty and the refineries are on
			// the fixed priority order even though the assemblers want
			// things - the status display shows "(Assembler all unknown
			// recipes)" to flag why.
			static public bool assemblerQueuesAllUnknown()
			{
				bool anyQueue = false;
				for (int i = 0; i < Program.assemblers.Count; i++)
				{
					var asm = Program.assemblers[i];
					if (asm.Mode != MyAssemblerMode.Assembly) continue;
					if (AsmDiscover.isDiscovering(asm)) continue;
					List<MyProductionItem> queue = new List<MyProductionItem>();
					asm.GetQueue(queue);
					if (queue.Count == 0) continue;
					anyQueue = true;
					for (int q = 0; q < queue.Count; q++)
					{
						MyItemType item;
						if (itemForBlueprint(queue[q].BlueprintId, out item)) return false;
					}
				}
				return anyQueue;
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
				// working copy of the ingot stock. As queues are walked in
				// order, satisfied stacks RESERVE their full need against it
				// (the assembler will consume those ingots), so the next
				// stack only sees what is actually left - two assemblers
				// queueing the same item both contribute their shortfall.
				Dictionary<MyItemType, double> workingStock = new Dictionary<MyItemType, double>();
				foreach (var kvp in Inventory.globalManifest.stuff)
					workingStock[kvp.Key] = (double)kvp.Value;

				// 1. ingot shortfall from the assembler queues (Assembly
				// mode only). Walk each queue from the head: an item whose
				// ingot needs are already covered gives the refineries
				// "nothing to do", so it is skipped and the NEXT stack is
				// considered. The first item with a real gap contributes its
				// per-ingot shortfall (not the full need) and the walk stops.
				Dictionary<MyItemType, double> ingotDemand = new Dictionary<MyItemType, double>();
				// how many units of the queued item the current stock can
				// support per ingredient (stock / per-unit need). The
				// ingredient with the LOWEST coverage is the binding
				// constraint for the next production cycle: no matter how
				// much of the others we have, the assembler cannot make
				// more units than the least-covered ingredient allows. The
				// refineries must therefore work on its ore first.
				Dictionary<MyItemType, double> ingotCoverage = new Dictionary<MyItemType, double>();
				for (int i = 0; i < Program.assemblers.Count; i++)
				{
					var asm = Program.assemblers[i];
					if (asm.Mode != MyAssemblerMode.Assembly) continue;
					if (AsmDiscover.isDiscovering(asm)) continue;
					List<MyProductionItem> queue = new List<MyProductionItem>();
					asm.GetQueue(queue);
					for (int q = 0; q < queue.Count; q++)
					{
						MyItemType item;
						if (!itemForBlueprint(queue[q].BlueprintId, out item)) continue;
						var comp = AsmLearn.compositionFor(item);
						if (comp.Count == 0) continue; // unknown composition -> skip this stack
						bool satisfied = true;
						Dictionary<MyItemType, double> shortfall = new Dictionary<MyItemType, double>();
						foreach (var ing in comp)
						{
							if (!ing.Key.GetItemInfo().IsIngot) continue; // refineries make ingots, not components
							double needed = (double)queue[q].Amount * (double)ing.Value;
							double stock = 0;
							workingStock.TryGetValue(ing.Key, out stock);
							if (stock < 0) stock = 0; // previous reservations may have gone negative
							double gap = needed - stock;
							if (gap > 0)
							{
								satisfied = false;
								shortfall[ing.Key] = gap;
								// coverage = how many units of the queued
								// item this stock supports (stock /
								// per-unit need); the MINIMUM across
								// contributing stacks wins
								double c = 0;
								ingotCoverage.TryGetValue(ing.Key, out c);
								double cov = stock / (double)ing.Value;
								if (c == 0 || cov < c) ingotCoverage[ing.Key] = cov;
							}
						}
						// reserve this stack's full need regardless: satisfied
						// stacks consume their ingots, unsatisfied ones will
						// once the refineries produce them
						foreach (var ing in comp)
						{
							if (!ing.Key.GetItemInfo().IsIngot) continue;
							double need = (double)queue[q].Amount * (double)ing.Value;
							double cur;
							workingStock.TryGetValue(ing.Key, out cur);
							workingStock[ing.Key] = cur - need;
						}
						if (satisfied) continue; // refineries have nothing to do for this stack
						foreach (var kvp in shortfall)
						{
							double d;
							ingotDemand.TryGetValue(kvp.Key, out d);
							ingotDemand[kvp.Key] = d + kvp.Value;
						}
						break; // first unsatisfied stack drives this assembler's demand
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

				// 3. attribute each ingot's shortfall to its single most
				// efficient source. Dividing the shortfall by EVERY ore's
				// ratio would inflate bad sources (e.g. stone at 0.03
				// iron/stone -> 186k "demand" to satisfy iron) and make the
				// refinery waste its time on them; the demand list must
				// point at the ore that actually satisfies the ingot.
				Dictionary<MyItemType, double> oreDemand = new Dictionary<MyItemType, double>();
				Dictionary<MyItemType, double> oreCoverage = new Dictionary<MyItemType, double>();
				foreach (var ing in ingotDemand)
				{
					MyItemType bestOre = default(MyItemType);
					double bestRatio = 0;
					foreach (var oreKvp in oreIngot)
					{
						double ratio;
						if (!oreKvp.Value.TryGetValue(ing.Key, out ratio) || ratio <= 0) continue;
						if (ratio > bestRatio) { bestRatio = ratio; bestOre = oreKvp.Key; }
					}
					if (bestRatio <= 0) continue;
					double d;
					oreDemand.TryGetValue(bestOre, out d);
					oreDemand[bestOre] = d + ing.Value / bestRatio;
					// an ore serving several ingots ranks by its most
					// binding (lowest-coverage) output
					double cov;
					if (!oreCoverage.TryGetValue(bestOre, out cov) || ingotCoverage[ing.Key] < cov)
						oreCoverage[bestOre] = ingotCoverage[ing.Key];
				}

				// 4. most binding first: the ingredient whose stock covers
				// the fewest future units is the bottleneck for the next
				// production cycle, so its ore leads. Ties by demand.
				return oreDemand.Keys
					.OrderBy(ore => oreCoverage[ore])
					.ThenByDescending(ore => oreDemand[ore])
					.ToList();
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
					refOre.Clear();
					for (int i = 0; i < Program.refineries.Count; i++)
					{
						var r = Program.refineries[i];
						if (r.IsProducing) refWorking++;
						else refIdle++;
						var item0 = r.InputInventory.GetItemAt(0);
						if (item0.HasValue && Inventory.getItemInfo(item0.Value.Type).IsOre)
							refOre.Add(item0.Value.Type);
						else
							refOre.Add(default(MyItemType));
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
