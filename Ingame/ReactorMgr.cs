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
			public void update()
			{
				if (_ticks % 60 * 3 != 0) return;
				//if (!gInv.hasUpdatedOnce) return;

				//if (gInv.updateCounter == curUpdate) return;//we only run this right after a full inventory update

				//curUpdate = gInv.updateCounter;

				// 1. Gather Data (and fix the empty reactor alignment bug)
				totalFuel = 0;
				fuelType = null;
				List<MyFixedPoint> fuelCounts = new List<MyFixedPoint>();

				for (int i = 0; i < Program.reactors.Count; i++)
				{
					var inv = Program.reactors[i].GetInventory();
					MyInventoryItem? itm = inv.GetItemAt(0);

					if (itm.HasValue)
					{
						if (fuelType == null) fuelType = itm.Value.Type; // Store the exact type of fuel
						totalFuel += itm.Value.Amount;
						fuelCounts.Add(itm.Value.Amount);
					}
					else
					{
						// CRITICAL: Add 0 so the fuelCounts index still perfectly matches Program.reactors[i]
						fuelCounts.Add(0);
					}
				}

				// 2. Rebalance Logic
				// Only proceed if we have at least 2 reactors, some fuel exists, and we know the fuel type
				if (Program.reactors.Count > 1 && totalFuel > 0 && fuelType.HasValue)
				{
					MyFixedPoint average = (MyFixedPoint)((double)totalFuel / Program.reactors.Count);

					// Define what "significantly less" means. 
					// If a reactor is within +/- 5 of the average, we leave it alone.
					MyFixedPoint tolerance = REACTOR_BALANCING_MARGIN;

					for (int i = 0; i < Program.reactors.Count; i++)
					{
						// Is this reactor significantly BELOW the average? (A "Receiver")
						if (fuelCounts[i] < (average - tolerance) || fuelCounts[i] < (MyFixedPoint)0.01d)
						{
							MyFixedPoint amountNeeded = average - fuelCounts[i];

							// Find other reactors that are significantly ABOVE the average (The "Donors")
							for (int j = 0; j < Program.reactors.Count; j++)
							{
								if (fuelCounts[j] > (average + tolerance))
								{
									MyFixedPoint amountAvailable = fuelCounts[j] - average;

									// Transfer the smaller amount between what the receiver needs and the donor can spare
									MyFixedPoint transferAmount = MyFixedPoint.Min(amountNeeded, amountAvailable);

									if (transferAmount > 0)
									{
										var donorInv = Program.reactors[j].GetInventory();
										var receiverInv = Program.reactors[i].GetInventory();

										// Use FindItem instead of GetItemAt(0) on the donor to guarantee we grab the uranium, 
										// just in case they have an unpulled empty casing or trash in slot 0
										MyInventoryItem? donorItem = donorInv.FindItem(fuelType.Value);

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

								// If the receiver is now satisfied, break out of the donor loop to save performance
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
