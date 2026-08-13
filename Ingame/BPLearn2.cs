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
		class BPLearn2
		{
			public IMyAssembler asm = null;
			public int lastCraft = 0;

			static MyDefinitionId nop = MyDefinitionId.Parse("MyObjectBuilder_BlueprintDefinition/SpaceCredit");


			public List<MyProductionItem> lastQueue = new List<MyProductionItem>();
			List<MyInventoryItem> lastItems = new List<MyInventoryItem>();
			float lastProgress = -1;
			static Profiler bpl = new Profiler("bpl");

			//int tick2 = 0;
			//int tick3 = 0;
			DateTime ltime = DateTime.Now;

			public void update()
			{
				bpl.s();
				//assemblers only tick once per second, so a faster observation is meaningless
			
				if (tick % 60 == 0 && /*(ltime- DateTime.Now).TotalMilliseconds >= 200*/ asm.Mode == MyAssemblerMode.Assembly)
				{
					ltime = DateTime.Now;
					var curProg = asm.CurrentProgress;
					List<MyProductionItem> queue = new List<MyProductionItem>();
					List<MyInventoryItem> items = new List<MyInventoryItem>();

					//tick2 += 1;
					//tick3 += 1;


					asm.GetQueue(queue);
					//todo: if get queue and unknown recipe in it, flush the output inventory of the assembler

					

					if (queue.Count != 0 || lastQueue.Count != 0)
					{
						asm.OutputInventory.GetItems(items);

						//this is because of nasty things like guns and tools that don't stack :|
						List<MyItemType> types = new List<MyItemType>();
						List<MyInventoryItem> itemsCompact = new List<MyInventoryItem>();
						foreach (var i in items)
						{
							var t = i.Type;
							if (types.Contains(t)) continue;
							types.Add(t);

							MyFixedPoint c = 0;
							foreach (var i2 in items)
							{
								if (i2.Type == t) c += i2.Amount;
							}
							if (c > 0)
							{
								itemsCompact.Add(new MyInventoryItem(i.Type, i.ItemId, c));
							}
						}
						items = itemsCompact;

						if (/*curProg < lastProgress && */lastQueue.Count > 0)
						{
							//log("tick2=" + tick2, LT.LOG_N);
							//tick2 = 0;

							MyDefinitionId recipe = nop;
							//compare production queue to last known state.
							//Check for any item with a decreased count or that has left the queue altogether
							//and save it
							foreach (var lastitem in lastQueue)
							{
								bool still_queued = false;
								foreach (var curitem in queue)
								{
									if (curitem.ItemId == lastitem.ItemId && curitem.BlueprintId == lastitem.BlueprintId)
									{
										still_queued = true;
										if (curitem.Amount < lastitem.Amount)
										{
											recipe = lastitem.BlueprintId;
											break;
										}
									}
								}
								if (recipe != nop) break;

								if (!still_queued)
								{
									recipe = lastitem.BlueprintId;
									break;
								}
							}

							if (recipe != nop)
							{

								//assuming we found the above, check for a newly appearing or count-increased item in the output inventory.
								//if progress has reset, a production order has shrunk or vanished, and a new item has appeared,
								///we can reasonably assume that the production order blueprint generates that item.
								MyDefinitionId itemdef = nop;
								foreach (var i in items)
								{
									bool newitem = true;
									foreach (var o in lastItems)
									{
										if (o.Type == i.Type) newitem = false;

										if (o.Type == i.Type && o.Amount < i.Amount)
										{
											try
											{
												var n = MyDefinitionId.Parse(i.Type.TypeId + "/" + i.Type.SubtypeId);
												itemdef = n;
												newitem = false;
												break;
											}
											catch (Exception) { }
										}
										//if (itembp != nop) break;
									}


									//if we found no increased item (the bp is nop) but we did notice a new kind of item, it's that one
									if (newitem)// && itembp == nop)
									{
										try
										{
											var n = MyDefinitionId.Parse(i.Type.TypeId + "/" + i.Type.SubtypeId);
											itemdef = n;
										}
										catch (Exception) { }
									}
									if (itemdef != nop) break;
								}
								if (recipe != nop && itemdef != nop)
								{
									lastCraft = tick;
									if (!Autocraft.blueprints.ContainsKey(itemdef))
									{
										log("Learned recipe " + itemdef.ToString() + ";" + recipe.ToString(), LT.LOG_N);
										Autocraft.addBP(itemdef, recipe);
									}
									//Autocraft.blueprints[itemdef] = recipe;
									//todo:flush output inventory here

								}

							}
						}
					}
					if (lastQueue.Count == 0) curProg = 0;


					lastProgress = curProg;
					lastQueue = queue;
					lastItems = items;
				}
				bpl.e();
			}
		}
	}
}

