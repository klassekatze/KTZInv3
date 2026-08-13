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
			}


			Inventory.InventoryManifest NonRefManifest = new Inventory.InventoryManifest();
			List<MyItemType> availOrePriority = new List<MyItemType>();
			int availOrePriorityNo1Index = -1;
			public void computeFactors()
			{
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
				foreach (var ore in orePriority)
				{
					MyFixedPoint amt = 0;
					NonRefManifest.stuff.TryGetValue(ore, out amt);
					if (amt > 0)
					{
						availOrePriority.Add(ore);
					}
				}
				if (availOrePriority.Count == 0) return;
				availOrePriorityNo1Index = orePriority.IndexOf(availOrePriority[0]);
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
								int idx1 = orePriority.IndexOf(item0.Value.Type);
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
