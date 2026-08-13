using Sandbox.Graphics;
using Sandbox.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
	public partial class Program : MyGridProgram
	{
		class AssemblerMgr
		{
			//List<BPLearn2> bplearners = new List<BPLearn2>();
			List<Asmstate> asmstates = new List<Asmstate>();
			class Asmstate
			{
				public BPLearn2 bpl = null;
				public int flushTick = 0;
				public int lastProduced = 0;
			}






			public AssemblerMgr()
			{
				foreach(var a in Program.assemblers)
				{
					var l = new BPLearn2();
					l.asm = a;
					var s = new Asmstate();
					s.bpl = l;
					asmstates.Add(s);
					//bplearners.Add(l);
				}
			}
			Dictionary<MyDefinitionId, List<IMyAssembler>> bpassemblers = new Dictionary<MyDefinitionId, List<IMyAssembler>>();

			int shuffleidx = 0;
			public void shuffleAssemblers()
			{
				shuffleidx = (shuffleidx + 1) % assemblers.Count;
				if (tick % 10 == 0 && assemblers.Count > 0)
				{
					var state = asmstates[shuffleidx];
					var asm = assemblers[shuffleidx];

					if (asm.IsQueueEmpty || asm.IsProducing || !asm.Enabled)
					{
						state.flushTick = state.lastProduced = tick;
					}
					else
					{
						if (tick - state.flushTick > 60 * 17)
						{
							log("flushing " + asm.CustomName);
							//IMyInventory input = null;
							//if (asm.Mode == MyAssemblerMode.Assembly) input = asm.InputInventory;
							//else input = asm.OutputInventory;
							List<MyInventoryItem> itms = new List<MyInventoryItem>();
							asm.InputInventory.GetItems(itms);
							List<MyInventoryItem> itms2 = new List<MyInventoryItem>();
							asm.OutputInventory.GetItems(itms2);
							itms.AddRange(itms2);
							foreach (var itm in itms)
							{
								Inventory.BlockInventory bi = Inventory.BlockInventory.getBI(asm);
								Inventory.expel(bi, itm.Type, itm.Amount, true);
							}
							state.flushTick = tick;
						}
						if (tick - state.lastProduced > 60 * 30)
						{
							log("rear shuffling " + asm.CustomName);
							List<MyProductionItem> queue = new List<MyProductionItem>();
							asm.GetQueue(queue);
							if (queue.Count > 1)
							{
								var po = queue[0];
								asm.RemoveQueueItem(0, po.Amount);
								asm.AddQueueItem(po.BlueprintId, po.Amount);
							}
							//asm.MoveQueueItemRequest(queue[0].ItemId, queue.Count-1);
							state.flushTick = state.lastProduced = tick;
						}
					}

					//reprioritize urgent fuel jobs
					if(gReactorMgr.fuelType.HasValue && gReactorMgr.totalFuel < 50)
					{
						List<MyProductionItem> queue = new List<MyProductionItem>();
						asm.GetQueue(queue);
						MyDefinitionId fuelbp;
						if(Autocraft.blueprints.TryGetValue(gReactorMgr.fuelType.Value,out fuelbp))
						{
							int fueljob = -1;
							for(int i =0; i < queue.Count; i++)
							{
								if(queue[i].BlueprintId == fuelbp)
								{
									fueljob = i;
									break;
								}
							}
							if (fueljob != -1 && fueljob > 0)
							{
								asm.ClearQueue();
								asm.AddQueueItem(queue[fueljob].BlueprintId, queue[fueljob].Amount);
								for (int i = 0; i < queue.Count; i++)
								{
									if(i != fueljob)asm.AddQueueItem(queue[i].BlueprintId, queue[i].Amount);
								}
							}
						}
						//..if()
					}
				}
			}
			//int lastshuffle = 0;
			//int shufflefreq = 60 * 2;
			//public void balanceAssemblers()
			//{
				
				/*


				if (tick % 60 == 0)
				{
					for (int i = 0; i < assemblers.Count; i++)
					{
						var state = asmstates[i];
						var asm = assemblers[i];

						
					}
				}
				*/
			//	balanceAssemblers(MyAssemblerMode.Assembly);
			//	balanceAssemblers(MyAssemblerMode.Disassembly);
			//}

			//i.e. if you say steel plate, assembling, it will erase any steel plate disassembly jobs
			//since disassembling directly contradicts the goal of assembling
			public void clearContradictingJobs(MyDefinitionId bp, MyAssemblerMode m)
			{
				foreach (var a in assemblers)
				{
					if (a.Mode != m)
					{
						List<MyProductionItem> queue = new List<MyProductionItem>();
						a.GetQueue(queue);
						for (var i = 0; i < queue.Count; i++)
						{
							var itm = queue[i];
							if(itm.BlueprintId == bp)
							{
								a.RemoveQueueItem(i, itm.Amount);
								queue.RemoveAt(i);
								i--;
							}
						}
					}
				}
			}


			public void balanceAssemblers(MyAssemblerMode m, Dictionary<MyDefinitionId, MyFixedPoint> set = null)
			{
				Dictionary<MyDefinitionId, MyFixedPoint> orders = new Dictionary<MyDefinitionId, MyFixedPoint>();
				
				//List<List<MyProductionItem>> queues = new List<List<MyProductionItem>>();
				Dictionary<IMyAssembler, List<MyProductionItem>> queues = new Dictionary<IMyAssembler, List<MyProductionItem>>();
				foreach (var a in assemblers)
				{
					if (a.Mode != m)
					{
						a.ClearQueue();
						a.Mode = m;
					}
					//if (a.Mode == m)
					{
						List<MyProductionItem> queue = new List<MyProductionItem>();
						a.GetQueue(queue);
						for (var i = 0; i < queue.Count; i++)
						{
							var itm = queue[i];
							if (itm.Amount < (MyFixedPoint)1)//because keen. look idk man
							{
								a.RemoveQueueItem(i, itm.Amount);
								queue.RemoveAt(i);
								i--;
							}
							else
							{
								var bp = itm.BlueprintId;
								if (orders.ContainsKey(bp)) orders[bp] += itm.Amount;
								else orders[bp] = itm.Amount;
							}
						}
						queues[a] = queue;
					}
				}

				if (set != null)
				{
					foreach (var kvp in set)
					{
						MyFixedPoint ct = 0;
						orders.TryGetValue(kvp.Key, out ct);
						if (m == MyAssemblerMode.Assembly && kvp.Value > ct) orders[kvp.Key] = kvp.Value;
						else if (m == MyAssemblerMode.Disassembly && -kvp.Value > ct) orders[kvp.Key] = -kvp.Value;
					}
				}

				foreach (var kvp in orders)
				{
					var bp = kvp.Key;
					var amt = kvp.Value;
					List<IMyAssembler> relevant_assemblers = new List<IMyAssembler>();
					//bool blueprintValid = false;
					foreach (var a in assemblers)
					{
						if (a.Mode == m)
						{
							if (a.CanUseBlueprint(bp))
							{
								relevant_assemblers.Add(a);
								//blueprintValid = true;
							}
						}
					}
					if (relevant_assemblers.Count == 0) continue;
					int divided = (int)amt / relevant_assemblers.Count;
					int remainder = (int)amt - (divided * (relevant_assemblers.Count-1));
					for(int i = 0; i < relevant_assemblers.Count; i++)
					{
						var asm = relevant_assemblers[i];
						var queue = queues[asm];

						var t_v = i == (relevant_assemblers.Count - 1) ? remainder : divided;

						if (t_v > -1 && t_v < 1) t_v = 0;

						MyProductionItem citem = new MyProductionItem();
						int idx = -1;

						for (var e = 0; e < queue.Count; e++)
						{
							var n = queue[e];
							if(n.BlueprintId == bp)
							{
								citem = n;
								idx = e;
								break;
							}
						}
						if (idx != -1)
						{
							var c_v = citem.Amount;
							var diff = t_v - c_v;

							if (Math.Abs((int)diff) >= 3)
							{
								//log("doin it: "+diff);
								asm.InsertQueueItem(idx, bp, diff);
							}
						}
						else if(t_v != 0)asm.AddQueueItem(bp, (MyFixedPoint)t_v);
					}
				}
			}
			//static Profiler shufP = new Profiler("asmshuf");
			//static Profiler balP = new Profiler("asmbal");
			int lbal = 0;

			public int updateCountsAsmDisasmChange = 0;
			public int lastUpdateCount = 0;
			public bool should_asm = false;
			public string asm_rsn = "";
			public bool should_disasm = false;
			public string disasm_rsn = "";

			// cached status counts, refreshed once per second (assemblers only
			// tick once per second anyway - faster observation is meaningless).
			// stalled = has orders in queue but not in a working state.
			public int asmWorking = 0;
			public int asmStalled = 0;
			public int asmIdle = 0;

			public void update()
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				foreach (var l in asmstates) l.bpl.update();

				if (tick % 60 == 0)
				{
					asmWorking = asmStalled = asmIdle = 0;
					foreach (var l in asmstates)
					{
						var a = l.bpl.asm;
						bool hasOrders = !a.IsQueueEmpty;
						if (hasOrders && a.IsProducing) asmWorking++;
						else if (hasOrders) asmStalled++;
						else asmIdle++;
					}
				}

				if (!gInv.hasUpdatedOnce) return;

				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.AsmShuffle) : false; }
				//shufP.s();
				if (tick % (60 * 15) == 0)
				{
					if(ASM_SHUFFLE)shuffleAssemblers();
				}
				//shufP.e();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.AsmShuffle) : false; }
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.AsmBalance) : false; }
				//balP.s();
				/*if (tick % (60 * 7) == 0)
				{
					//if(ASM_FLUSH)balanceAssemblers();
				}*/
				//balP.e();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.AsmBalance) : false; }

				

				if (tick % 60 == 0 && tick - gInv.lastUpdateTick <= 60)
				{
					Dictionary<MyDefinitionId, MyFixedPoint> production = new Dictionary<MyDefinitionId, MyFixedPoint>();
					foreach (var l in asmstates)
					{
						foreach (var i in l.bpl.lastQueue)
						{
							if (!production.ContainsKey(i.BlueprintId)) production.Add(i.BlueprintId, i.Amount);
							else production[i.BlueprintId] += i.Amount;
						}
					}
					Dictionary<MyDefinitionId, MyFixedPoint> orders = new Dictionary<MyDefinitionId, MyFixedPoint>();
					int asmjobs = 0;
					int dasmjobs = 0;
					asm_rsn = disasm_rsn = "";
					foreach (var kvp in Autocraft.quotas_bp)
					{
						var itembp = kvp.Key;
						var recipebp = Autocraft.blueprints[itembp];

						var desired_amt = kvp.Value;
						//var assembling_amt = production.ContainsKey(recipebp) ? production[recipebp] : 0;
						MyFixedPoint current_amt = 0;
						Inventory.globalManifest.stuff.TryGetValue((MyItemType)itembp, out current_amt);

						if (ASSEMBLE && current_amt + ASSEMBLE_MARGIN < desired_amt && (desired_amt - current_amt) != 0)
						{
							orders[recipebp] = desired_amt - current_amt;
							if (asmjobs == 0) asm_rsn = itembp.SubtypeId.ToString() + ": " + (desired_amt - current_amt);
							asmjobs += 1;
						}
						if (DISASSEMBLE && current_amt - ASSEMBLE_MARGIN > desired_amt && (desired_amt - current_amt) != 0)
						{
							orders[recipebp] = desired_amt - current_amt;
							if (dasmjobs == 0) disasm_rsn = itembp.SubtypeId.ToString()+": " + (desired_amt - current_amt);
							dasmjobs += 1;
						}
					}
					if (gReactorMgr.fuelType.HasValue && gReactorMgr.totalFuel < 10)
					{
						dasmjobs = 0;//we don't disassemble if low on fuel, we make fuel.
					}
					if ((asmjobs > 0 != should_asm) || (dasmjobs > 0 != should_disasm))
					{
						should_asm = asmjobs > 0;
						should_disasm = dasmjobs > 0;
						updateCountsAsmDisasmChange = 0;
						lastUpdateCount = gInv.updateCounter;
					}
					

					if (lastUpdateCount != gInv.updateCounter)
					{
						updateCountsAsmDisasmChange++;
						lastUpdateCount = gInv.updateCounter;
					}
					///int updateCountsAsmDisasmChange = 0;
						//bool should_asm = false;
						//bool should_disasm = false;


					if (orders.Count > 0 && updateCountsAsmDisasmChange > 2)
					{
						if (asmjobs > 0 && (dasmjobs == 0 || PRIORITY_DISASSEMBLE == false))
						{
							balanceAssemblers(MyAssemblerMode.Assembly, orders);
						}
						else if (dasmjobs > 0 && (asmjobs == 0 || PRIORITY_DISASSEMBLE == true))
						{
							balanceAssemblers(MyAssemblerMode.Disassembly, orders);
						}
						/*if(asmjobs == 0 && dasmjobs != 0)
						{
							foreach (var s in assemblers) if(s.IsQueueEmpty)s.Mode = MyAssemblerMode.Disassembly;
						}else if (asmjobs != 0 && dasmjobs == 0)
						{
							foreach (var s in assemblers) if (s.IsQueueEmpty) s.Mode = MyAssemblerMode.Assembly;
						}*/

						//PRIORITY_DISASSEMBLE
						/*bool has_asm_orders = false;
						bool has_disasm_orders = false;
						foreach (var kvp in orders)
						{
							if (ASSEMBLE && kvp.Value > 0) has_asm_orders = true;
						//	*///else if (DISASSEMBLE && kvp.Value < 0) has_disasm_orders = true;
							  //}
							  //*/

						/*if(asmjobs > 0 && (dasmjobs == 0 || PRIORITY_DISASSEMBLE == false))
						{
							foreach (var kvp in orders)
							{
								if (kvp.Value > 0) clearContradictingJobs(kvp.Key, MyAssemblerMode.Assembly);
							}
							balanceAssemblers(MyAssemblerMode.Assembly, orders);
						}
						else if (dasmjobs > 0 && (asmjobs == 0 || PRIORITY_DISASSEMBLE == true))
						{
							foreach (var kvp in orders)
							{
								if (kvp.Value < 0) clearContradictingJobs(kvp.Key, MyAssemblerMode.Disassembly);
							}
							balanceAssemblers(MyAssemblerMode.Disassembly, orders);
						}*/


						/*if (ASSEMBLE)
						{
							foreach(var kvp in orders)
							{
								if (kvp.Value > 0) clearContradictingJobs(kvp.Key,MyAssemblerMode.Assembly);
							}
							balanceAssemblers(MyAssemblerMode.Assembly, orders);
						}
						if (DISASSEMBLE)
						{
							foreach (var kvp in orders)
							{
								if (kvp.Value < 0) clearContradictingJobs(kvp.Key, MyAssemblerMode.Disassembly);
							}
							balanceAssemblers(MyAssemblerMode.Disassembly, orders);
						}*/
					}else if (orders.Count == 0)
					{
						for (int i = 0; i < assemblers.Count; i++)
						{
							if (assemblers[i].Mode != MyAssemblerMode.Assembly) assemblers[i].Mode = MyAssemblerMode.Assembly;
						}
					}

				}
				//if (tick % (60 * 60 * 10) == 0)
				//{
					/*foreach(var asm in assemblers)
					{
						List<MyInventoryItem> items = new List<MyInventoryItem>();
						//asm.GetInventory(0);
						asm.InputInventory.GetItems(items);
						foreach(var i in items) invInterface_noasm.TransferItemTo
					}*/
					//invInterface_noasm


					/*List<MyInventoryItem> items = new List<MyInventoryItem>();

					//tick2 += 1;
					//tick3 += 1;


					asm.GetQueue(queue);
					//todo: if get queue and unknown recipe in it, flush the output inventory of the assembler



					if (queue.Count != 0 || lastQueue.Count != 0)
					{
						asm.OutputInventory.GetItems(items);
					}*/
			}
		}
	}
}
