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
			//static Profiler bpl = new Profiler("bpl");

			//int tick2 = 0;
			//int tick3 = 0;
			DateTime ltime = DateTime.Now;

			public void update()
			{
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.BpLearn) : false; }
				//bpl.s();
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
						//compact: sum amounts per type in one pass (was O(n^2) via
						//types.Contains + inner re-scan; same result: first-seen
						//order, first-seen ItemId, summed amount)
						List<MyItemType> types = new List<MyItemType>();
						List<MyInventoryItem> itemsCompact = new List<MyInventoryItem>();
						Dictionary<MyItemType, MyFixedPoint> sums = new Dictionary<MyItemType, MyFixedPoint>();
						Dictionary<MyItemType, uint> firstIds = new Dictionary<MyItemType, uint>();
						foreach (var i in items)
						{
							var t = i.Type;
							MyFixedPoint cur;
							if (sums.TryGetValue(t, out cur))
							{
								sums[t] = cur + i.Amount;
							}
							else
							{
								types.Add(t);
								sums[t] = i.Amount;
								firstIds[t] = i.ItemId;
							}
						}
						foreach (var t in types)
						{
							var c = sums[t];
							if (c > 0)
							{
								itemsCompact.Add(new MyInventoryItem(t, firstIds[t], c));
							}
						}
						items = itemsCompact;

						if (/*curProg < lastProgress && */lastQueue.Count > 0)
						{
							//log("tick2=" + tick2, LT.LOG_N);
							//tick2 = 0;

							// Attribution rule (mirrors RefLearn): only learn
							// from UNAMBIGUOUS windows. The assembler can
							// complete several queue items within one
							// 1-second observation (the game's production
							// loop advances past items and produces whatever
							// it can), so when MULTIPLE queue items changed
							// (decreased or vanished) or MULTIPLE output
							// types increased, the pairing would be
							// arbitrary -> skip the window instead of
							// learning a wrong association. This is what
							// caused the fast-crafting mislearns (item linked
							// to a different item's blueprint).
							MyDefinitionId recipe = nop;
							int queueChanges = 0;
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
											queueChanges++;
											if (queueChanges == 1) recipe = lastitem.BlueprintId;
										}
										break;
									}
								}
								if (!still_queued)
								{
									queueChanges++;
									if (queueChanges == 1) recipe = lastitem.BlueprintId;
								}
							}

							// only if exactly one queue item changed is the
							// output attributable; then it must also be
							// exactly one output type that increased
							MyDefinitionId itemdef = nop;
							int outputIncreases = 0;
							if (queueChanges == 1 && recipe != nop)
							{
								//check for a newly appearing or count-increased item in the output inventory.
								//if a production order has shrunk or vanished, and a new item has appeared,
								///we can reasonably assume that the production order blueprint generates that item.
								foreach (var i in items)
								{
									bool newitem = true;
									bool increased = false;
									foreach (var o in lastItems)
									{
										if (o.Type == i.Type)
										{
											newitem = false;
											if (o.Amount < i.Amount)
											{
												increased = true;
												break;
											}
										}
									}
									if (newitem || increased)
									{
										outputIncreases++;
										if (outputIncreases == 1)
										{
											try
											{
												itemdef = MyDefinitionId.Parse(i.Type.TypeId + "/" + i.Type.SubtypeId);
											}
											catch (Exception) { }
										}
									}
								}
							}
							if (recipe != nop && itemdef != nop && queueChanges == 1 && outputIncreases == 1)
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
					if (lastQueue.Count == 0) curProg = 0;


					lastProgress = curProg;
					lastQueue = queue;
					lastItems = items;
				}
				//bpl.e();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.BpLearn) : false; }
			}
		}
	}
}

