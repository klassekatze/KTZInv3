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
				if (!MANAGE_REACTORS) return;
				// work cadence on `tick` (executed ticks only) - NOT `_ticks`
				// (the sleep counter): a skip window advances _ticks past the
				// modulo point without executing any work, which would skip
				// the reactor balancing cycle entirely
				if (tick % (60 * 3) != 0) return;
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
				// other. Empty reactors are assigned to a group by what their
				// inventory accepts (GetAcceptedItems) - a reactor only accepts
				// the fuels it can burn, so an empty reactor's fuel type is
				// knowable even on a mixed-fuel grid.
				if (n > 1 && totalFuel > 0 && fuelType.HasValue)
				{
					// group reactor indices by their slot-0 fuel type
					Dictionary<MyItemType, List<int>> groups = new Dictionary<MyItemType, List<int>>();
					for (int i = 0; i < n; i++)
					{
						MyItemType t;
						if (hasFuel[i])
						{
							t = fuelTypes[i];
						}
						else
						{
							// empty reactor: find the first fuel type it accepts
							// that is actually present on the grid; if it accepts
							// nothing we have fuel for, skip it entirely
							List<MyItemType> accepted = new List<MyItemType>();
							Program.reactors[i].GetInventory().GetAcceptedItems(accepted);
							t = default(MyItemType);
							bool found = false;
							foreach (var at in accepted)
							{
								if (fuelByType.ContainsKey(at))
								{
									t = at;
									found = true;
									break;
								}
							}
							if (!found) continue;
						}
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

						// receivers: fuel significantly below the group average.
						// A reactor holding less than one fuel item is
						// effectively empty (the tail of a spent rod) and is
						// ALWAYS a receiver, even when the imbalance is below
						// the margin: the balancer's primary purpose is
						// redistributing fuel when one reactor took it all,
						// and the absolute margin (REACTOR_BALANCING_MARGIN)
						// would otherwise block that entirely whenever the
						// grid total is below ~2x the margin (live: 30.88 vs
						// 0.998 - avg 15.9, donor needs >40.9 -> nothing moves).
						foreach (var i in idxs)
						{
							bool effectivelyEmpty = fuelCounts[i] < (MyFixedPoint)1.0d;
							if (effectivelyEmpty || fuelCounts[i] < (average - tolerance))
							{
								MyFixedPoint amountNeeded = average - fuelCounts[i];

								// donors: same fuel type. When the receiver is
								// effectively empty, any reactor above the
								// average gives its surplus (imbalance is
								// absolute, not margin-gated); otherwise the
								// margin still applies so slight unevenness
								// between fueled reactors doesn't churn fuel.
								foreach (var j in idxs)
								{
									if (i == j) continue;
									bool isDonor = effectivelyEmpty
										? fuelCounts[j] > average
										: fuelCounts[j] > (average + tolerance);
									if (isDonor)
									{
										MyFixedPoint amountAvailable = fuelCounts[j] - average;
										MyFixedPoint transferAmount = MyFixedPoint.Min(amountNeeded, amountAvailable);

										if (transferAmount > 0)
										{
											var donorInv = Program.reactors[j].GetInventory();
											var receiverInv = Program.reactors[i].GetInventory();

											// cap by the receiver's free space, same as
											// Inventory's expel/transfer: the game silently
											// clamps a transfer to what fits, so book the
											// amount that actually CAN fit up front.
											var nfo = Inventory.getItemInfo(type);
											MyFixedPoint maxAccept = (receiverInv.MaxVolume - receiverInv.CurrentVolume) * (MyFixedPoint)(1.0 / nfo.Volume);
											if (!nfo.UsesFractions) maxAccept = MyFixedPoint.Floor(maxAccept + (MyFixedPoint)0.001);
											if (transferAmount > maxAccept) transferAmount = maxAccept;

											// FindItem instead of GetItemAt(0) on the donor to
											// guarantee we grab the right fuel, just in case
											// they have an unpulled empty casing or trash in slot 0
											MyInventoryItem? donorItem = donorInv.FindItem(type);

											if (donorItem.HasValue && transferAmount > 0)
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
