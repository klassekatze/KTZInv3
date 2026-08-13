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
		class ReactorMgr
		{
			//int curUpdate = -1;

			public MyItemType? fuelType = null;
			public MyFixedPoint totalFuel = 0;

			// per-type fuel totals for the status display, rebuilt each update.
			// key = fuel item type; value = total amount across all reactors.
			public Dictionary<MyItemType, MyFixedPoint> fuelByType = new Dictionary<MyItemType, MyFixedPoint>();
			public void update()
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				if (_ticks % (60 * 3) != 0) return;
				//if (!gInv.hasUpdatedOnce) return;

				//if (gInv.updateCounter == curUpdate) return;//we only run this right after a full inventory update

				//curUpdate = gInv.updateCounter;

				// 1. Gather Data (and fix the empty reactor alignment bug)
				totalFuel = 0;
				fuelType = null;
				fuelByType.Clear();
				int n = Program.reactors.Count;
				// per-reactor fuel state, parallel to Program.reactors
				List<MyFixedPoint> fuelCounts = new List<MyFixedPoint>(n);
				List<MyItemType> fuelTypes = new List<MyItemType>(n);
				List<bool> hasFuel = new List<bool>(n);

				for (int i = 0; i < n; i++)
				{
					var inv = Program.reactors[i].GetInventory();
					MyInventoryItem? itm = inv.GetItemAt(0);

					if (itm.HasValue)
					{
						fuelCounts.Add(itm.Value.Amount);
						fuelTypes.Add(itm.Value.Type);
						hasFuel.Add(true);
						totalFuel += itm.Value.Amount;
						if (fuelType == null) fuelType = itm.Value.Type; // dominant fuel fallback: first seen

						// per-type totals for the status display: sum the slot-0
						// fuel across reactors (a reactor only ever holds one
						// fuel type in slot 0)
						MyFixedPoint cur;
						if (fuelByType.TryGetValue(itm.Value.Type, out cur)) fuelByType[itm.Value.Type] = cur + itm.Value.Amount;
						else fuelByType[itm.Value.Type] = itm.Value.Amount;
					}
					else
					{
						// CRITICAL: Add 0 so the fuelCounts index still perfectly matches Program.reactors[i]
						fuelCounts.Add(0);
						fuelTypes.Add(default(MyItemType));
						hasFuel.Add(false);
					}
				}

				// dominant fuel type = the one with the most fuel total; exposed
				// as fuelType/totalFuel for the assembler low-fuel checks
				// (reprioritize fuel jobs / don't disassemble when the grid's
				// primary fuel runs low)
				{
					MyFixedPoint dominantAmt = 0;
					foreach (var kvp in fuelByType)
					{
						if (kvp.Value > dominantAmt)
						{
							dominantAmt = kvp.Value;
							fuelType = kvp.Key;
						}
					}
					totalFuel = dominantAmt;
				}

				// 2. Rebalance Logic - grouped by fuel type. A reactor only ever
				// holds one fuel type in slot 0, but different reactors may burn
				// different fuels (SDX2: UraniumItem, UraniumB, sdx_itemReactorFuel),
				// so only reactors sharing a fuel type are balanced against each
				// other. Empty reactors join the group only when exactly one fuel
				// type exists on the grid (that's the empty-reactor alignment fix);
				// with mixed fuels their fuel type is unknowable, so they are left
				// to the conveyor/priming systems.
				if (n > 1 && totalFuel > 0 && fuelType.HasValue)
				{
					// group reactor indices by their slot-0 fuel type
					Dictionary<MyItemType, List<int>> groups = new Dictionary<MyItemType, List<int>>();
					bool singleFuelType = fuelByType.Count == 1;
					for (int i = 0; i < n; i++)
					{
						MyItemType t = hasFuel[i] ? fuelTypes[i] : fuelType.Value;
						if (!hasFuel[i] && !singleFuelType) continue; // unknown fuel with mixed grid
						if (!groups.ContainsKey(t)) groups[t] = new List<int>();
						groups[t].Add(i);
					}

					foreach (var g in groups)
					{
						var type = g.Key;
						var idxs = g.Value;
						if (idxs.Count < 2) continue;

						MyFixedPoint groupTotal = 0;
						foreach (var i in idxs) groupTotal += fuelCounts[i];
						MyFixedPoint average = (MyFixedPoint)((double)groupTotal / idxs.Count);
						MyFixedPoint tolerance = REACTOR_BALANCING_MARGIN;

						// receivers: fuel significantly below the group average
						foreach (var i in idxs)
						{
							if (fuelCounts[i] < (average - tolerance) || fuelCounts[i] < (MyFixedPoint)0.01d)
							{
								MyFixedPoint amountNeeded = average - fuelCounts[i];

								// donors: same fuel type, significantly above average
								foreach (var j in idxs)
								{
									if (i == j) continue;
									if (fuelCounts[j] > (average + tolerance))
									{
										MyFixedPoint amountAvailable = fuelCounts[j] - average;
										MyFixedPoint transferAmount = MyFixedPoint.Min(amountNeeded, amountAvailable);

										if (transferAmount > 0)
										{
											var donorInv = Program.reactors[j].GetInventory();
											var receiverInv = Program.reactors[i].GetInventory();

											// FindItem instead of GetItemAt(0) on the donor to
											// guarantee we grab the right fuel, just in case
											// they have an unpulled empty casing or trash in slot 0
											MyInventoryItem? donorItem = donorInv.FindItem(type);

											if (donorItem.HasValue)
											{
												donorInv.TransferItemTo(receiverInv, donorItem.Value, transferAmount);

												// Update our local counts so we don't over-transfer in this same tick
												fuelCounts[j] -= transferAmount;
												fuelCounts[i] += transferAmount;
												amountNeeded -= transferAmount;
											}
										}
									}
									if (amountNeeded <= 0) break;
								}
								if (amountNeeded <= 0) break;
							}
							}
							}
							}

							/*MyItemType fuelType = new MyItemType();
				MyFixedPoint fuelCount = 0;
				List<MyFixedPoint> fuelCounts = new List<MyFixedPoint>();
				for (int i = 0; i < Program.reactors.Count; i++)
				{
					var r = Program.reactors[i];

					MyInventoryItem? itm = r.GetInventory().GetItemAt(0);

					if(itm.HasValue)
					{
						fuelType = itm.Value.Type;
						fuelCount += itm.Value.Amount;
						fuelCounts.Add(itm.Value.Amount);
					}
				}
				if (fuelCount > 0)
				{
					for (int i = 0; i < Program.reactors.Count; i++)
					{
						var cr = fuelCounts[i];
						
						//...


					}
					//
					if(false)
					{
						MyInventoryItem? itm = Program.reactors[0].GetInventory().GetItemAt(0);
						MyFixedPoint amount = 2;
						Program.reactors[0].GetInventory().TransferItemTo(Program.reactors[1].GetInventory(), itm.Value, amount);
						Program.reactors[0].GetInventory().TransferItemFrom(Program.reactors[1].GetInventory(), itm.Value, amount);
					}
					
				}*/
			}
		}

	}
}
