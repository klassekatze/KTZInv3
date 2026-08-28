using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using VRage;
using VRage.Game.ModAPI.Ingame;
using VRageRender.Voxels;

namespace IngameScript
{
	partial class Program : MyGridProgram
	{
		public class Inventory
		{
			public static InventoryManifest globalManifest = new InventoryManifest();
			public static InventoryManifest gridManifest = new InventoryManifest();
			public static Dictionary<string, MyFixedPoint> nonFractionalMinMarginByCat = new Dictionary<string, MyFixedPoint>();
			public static HashSet<MyItemType> encounteredTypes = new HashSet<MyItemType>();

			static Dictionary<string, MyItemType> typeTable = new Dictionary<string, MyItemType>();
			static public MyItemType getType(string type, string subtype)
			{//can throw exceptions, MyItemType is fiddly
				var k = type + "/" + subtype;
				if (typeTable.ContainsKey(k)) return typeTable[k];
				else
				{
					return typeTable[k] = new MyItemType(type, subtype);
				}
			}

			// MyItemInfo cache: GetItemInfo() is a game API call (dictionary
			// lookup + item definition fetch). Item definitions are static per
			// session, so cache the result per MyItemType.
			static Dictionary<MyItemType, MyItemInfo> itemInfoCache = new Dictionary<MyItemType, MyItemInfo>();
			static public MyItemInfo getItemInfo(MyItemType t)
			{
				MyItemInfo nfo;
				if (itemInfoCache.TryGetValue(t, out nfo)) return nfo;
				nfo = t.GetItemInfo();
				itemInfoCache[t] = nfo;
				return nfo;
			}

			//there's some giga-fucky shit going on with tanks
			static List<string> unstackhardcode = new List<string>(){
			"MyObjectBuilder_OxygenContainerObject",
			"MyObjectBuilder_GasContainerObject",
			"MyObjectBuilder_PhysicalGunObject",
			"MyObjectBuilder_PhysicalObject",
			"MyObjectBuilder_Datapad",
			};

			static Dictionary<string, string> cattocargo = new Dictionary<string, string>()
				{
					{"MyObjectBuilder_OxygenContainerObject","Bottles"},
					{"MyObjectBuilder_GasContainerObject","Bottles"},

					{"MyObjectBuilder_PhysicalGunObject","Tools"},
					{"MyObjectBuilder_PhysicalObject","Tools"},//space credit
					{"MyObjectBuilder_ConsumableItem","Tools"},//cola, coffee
					{"MyObjectBuilder_Datapad","Tools"},//datapad duh

					{"MyObjectBuilder_AmmoMagazine","Ammo"},
					{"MyObjectBuilder_Ore","Ores"},
					{"MyObjectBuilder_Ingot","Ingots"},
					{"MyObjectBuilder_Component","Components"},
				};
			public static string cargokeywordbytype(string type)
			{
				string r = "Unknown";
				cattocargo.TryGetValue(type, out r);
				return r;
			}

			//static bool sortProductionInput = false;
			static bool treatBlankAsAlltype = false;
			public class InventoryManifest
			{
				


				public InventoryManifest()
				{

				}

				public Dictionary<MyItemType, MyFixedPoint> stuff = new Dictionary<MyItemType, MyFixedPoint>();
				public MyFixedPoint maxVolume;
				public MyFixedPoint freeVolume;
				public Dictionary<string, MyFixedPoint> typeVolume = new Dictionary<string, MyFixedPoint>();
				public void set(BlockInventory bi)
				{
					stuff.Clear();
					maxVolume = freeVolume = 0;

					var invs = bi.getSortedInventories(false);
					int merges = 0;
					//const int MAX_MERGES = 10;
					DateTime b4 = DateTime.Now;
					foreach (var nv in invs)
					{
						var mv = nv.MaxVolume;
						var cv = nv.CurrentVolume;
						maxVolume += mv;
						freeVolume += mv - cv;
						List<MyInventoryItem> itms = new List<MyInventoryItem>();
						nv.GetItems(itms);
						Dictionary<MyItemType, int> lItem = new Dictionary<MyItemType, int>();
						
						for(int i = itms.Count-1; i >= 0; i--)
						{
							var it = itms[i];
							//stack deduplication
							if (MERGE_STACKS && merges < MAX_TRANSFERS_PER_OP && lItem.ContainsKey(it.Type))
							{
								var stackable = !unstackhardcode.Contains(it.Type.TypeId);
								if (stackable)
								{
									var lpos = lItem[it.Type];
									var nfo = getItemInfo(it.Type);
									var lit = itms[lpos];
									if (it.Amount + lit.Amount < nfo.MaxStackAmount)
									{
										log(it.Type.SubtypeId + " msa " + nfo.MaxStackAmount + " stacking now ");
										nv.TransferItemTo(nv, lpos, i, true);
										merges++;
										if ((DateTime.Now - b4).TotalMilliseconds > MAX_TRANSFER_MS) merges = MAX_TRANSFERS_PER_OP;
									}
								}
							}
							lItem[it.Type] = i;

							//manifest generate
							if (!stuff.ContainsKey(it.Type)) stuff[it.Type] = it.Amount;
							else stuff[it.Type] += it.Amount;
							//typeVolume was write-only (never read by any consumer) and
							//cost a GetItemInfo() game API call per item per manifest
							//build - removed as dead work.
							}
					}
					if (merges > 0)
					{
						log(merges + " merges in " + bi.b.CustomName + " took " + (DateTime.Now - b4).TotalMilliseconds + "ms");
					}

					//very ugly.
					foreach (var kvp in stuff)
					{
						var k = kvp.Key;
						if(!encounteredTypes.Contains(k))
						{
							encounteredTypes.Add(k);
							MyFixedPoint minVol = (MyFixedPoint)0.01;
							var kinfo = getItemInfo(k); if (!kinfo.UsesFractions) minVol = (MyFixedPoint)kinfo.Volume;
							var cat = cargokeywordbytype(k.TypeId);
							MyFixedPoint kval = 0;
							nonFractionalMinMarginByCat.TryGetValue(cat, out kval);
							if (minVol > kval) kval = minVol;
							nonFractionalMinMarginByCat[cat] = kval;
						}
					}
				}
				public void sub(InventoryManifest o)
				{//if we don't even have the thing being subtracted nothing will be subtracted
					if (o == null) return;

					List<MyItemType> del = new List<MyItemType>();
					foreach (var kvp in o.stuff)
					{
						if (stuff.ContainsKey(kvp.Key))
						{
							var nv = stuff[kvp.Key] - kvp.Value;
							if (nv > 0) stuff[kvp.Key] = nv;
							else del.Add(kvp.Key);
						}
					}
					foreach (var k in del) stuff.Remove(k);
				}
				public void add(InventoryManifest o)
				{
					if (o == null) return;
					foreach (var kvp in o.stuff)
					{
						if (stuff.ContainsKey(kvp.Key)) stuff[kvp.Key] += kvp.Value;
						else stuff[kvp.Key] = kvp.Value;
					}
				}
				public bool equals(InventoryManifest o)
				{
					if (o == null || this.stuff.Count != o.stuff.Count) return false;


					foreach(var kvp in stuff)
					{
						MyFixedPoint v = 0;
						if (!o.stuff.TryGetValue(kvp.Key, out v)) return false;
						else if (kvp.Value != v) return false;
					}
					foreach(var kvp in o.stuff)
					{
						if (!stuff.ContainsKey(kvp.Key)) return false;
					}
					return true;
				}
			}

			static public List<PriorityAggregate> prAggs = new List<PriorityAggregate>();
			static public PriorityAggregate getPI(int p)
			{
				foreach (var pr in prAggs) if (pr.priority == p) return pr;
				var x = new PriorityAggregate();
				x.priority = p;
				prAggs.Add(x);
				prAggs.Sort();
				return x;
			}
			static PriorityAggregate higherPriorityWithRoomFor(BlockInventory bi, string category, HashSet<BlockInventory> deadEnds = null)
			{
				//PriorityAggregate pi = null;
				var pidx = 0;
				for(int i =0; i < prAggs.Count; i++)
				{
					var pr = prAggs[i];
					if (pr.priority == bi.priority)
					{
						//pi = pr;
						pidx = i;
						break;
					}
				}

				for (int i = 0; i < pidx; i++)
				{
					var c = prAggs[i];
					MyFixedPoint minmargin = 0;
					nonFractionalMinMarginByCat.TryGetValue(category, out minmargin);
					//per-container check: the aggregate must contain a live (non-dead-end)
					//container that accepts this category with at least the minimum margin free.
					foreach (var b in c.bis)
					{
						if (deadEnds != null && deadEnds.Contains(b)) continue;
						if (b.categories.Contains(category) && b.manifest.freeVolume >= minmargin) return c;
					}
				}
				return null;
			}
			public class PriorityAggregate : IComparable<PriorityAggregate>
			{
				int IComparable<PriorityAggregate>.CompareTo(PriorityAggregate y)
				{
					var x = this;
					return x.priority.CompareTo(y.priority);
				}
				public List<BlockInventory> bis = new List<BlockInventory>();
				public int priority = 0;
				public Dictionary<string, MyFixedPoint> typeVolumeFree = new Dictionary<string, MyFixedPoint>();
				public List<string> categories = new List<string>();
				public void update()
				{
					typeVolumeFree.Clear();
					categories.Clear();
					foreach (var bi in bis)
					{
						
						foreach (var c in bi.categories)
						{
							if (!categories.Contains(c)) categories.Add(c);
							MyFixedPoint v = 0;
							typeVolumeFree.TryGetValue(c, out v);
							//for our purposes, we are recording the largest free volume in a single container in the set that accepts this category.
							if (bi.manifest.freeVolume > v)
							{
								typeVolumeFree[c] = bi.manifest.freeVolume;
							}
						}
					}
				}
			}

			public class BlockInventory : IComparable<BlockInventory>
			{

				int IComparable<BlockInventory>.CompareTo(BlockInventory y)
				{
					var x = this;
					if (x.priority == y.priority)
					{
						return x.idx.CompareTo(y.idx);
					}
					return x.priority.CompareTo(y.priority);
				}

				public static List<BlockInventory> bPriorityList = new List<BlockInventory>();
				public static Dictionary<IMyTerminalBlock, BlockInventory> bIDict = new Dictionary<IMyTerminalBlock, BlockInventory>();
				public static BlockInventory getBI(IMyTerminalBlock b)
				{
					BlockInventory r = null;
					bIDict.TryGetValue(b, out r);
					if (r == null) r = new BlockInventory(b);
					return r;
				}
				const string bpprefix = "MyObjectBuilder_";
				const string everything = "alltypes";


				public static int idl = 0;
				public int idx = 0;
				public BlockInventory(IMyTerminalBlock b)
				{
					this.b = b;

					bPriorityList.Add(this);
					bIDict[b] = this;
					idx = idl;
					idl++;


					for (var i = 0; i < b.InventoryCount; i++)
					{
						sortedInventories.Add(b.GetInventory(i));
					}
					if (b is IMyProductionBlock)
					{
						isProduction = true;
						var p = (IMyProductionBlock)b;
						sortedInventoriesNoInput.Add(p.OutputInventory);
						sortedInventoriesNoOutput.Add(p.InputInventory);
						if (b is IMyAssembler)
						{
							isAssembler = true;
							asmref = (IMyAssembler)b;
						}
					}
					else
					{
						sortedInventoriesNoInput.AddRange(sortedInventories);
					}
				}
				public IMyTerminalBlock b = null;
				public InventoryManifest manifest = null;

				public List<string> categories = new List<string>();

				List<IMyInventory> sortedInventoriesNoInput = new List<IMyInventory>();
				List<IMyInventory> sortedInventoriesNoOutput = new List<IMyInventory>();
				List<IMyInventory> sortedInventories = new List<IMyInventory>();
				public List<IMyInventory> getSortedInventories(bool inc_input)
				{
					if (inc_input) return sortedInventories;
					else
					{
						if (asmref != null && asmref.Mode == MyAssemblerMode.Disassembly) return sortedInventoriesNoOutput;
						return sortedInventoriesNoInput;
					}

				}
				public bool isProduction = false;
				public bool isAssembler = false;
				IMyAssembler asmref = null;


				public Dictionary<MyItemType, MyFixedPoint> stocktargets = new Dictionary<MyItemType, MyFixedPoint>();
				public bool special = false;
				public bool locked = true;//we don't move shit to shit until first updateP
				public bool hidden = false;
				public bool holdall = false;
				const int default_p = 100000;
				public int priority = int.MaxValue;
				public string lastCD = "-31234";
				public string lastN = "-234523";

				void _locked()
				{
					locked = true;
					special = false;
				}
				void _hidden()
				{
					locked = true;
					hidden = true;
					special = false;
				}

				public void updateP()
				{
					{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
					if (b.CustomName != lastN)
					{
						lastN = b.CustomName;

						var lpriority = priority;
						//var PI = getPI(priority);
						//PI.bis.Remove(this);

						priority = default_p;
						special = false;
						locked = false;
						hidden = false;
						holdall = false;
						var t = lastN.Split(' ', '.');
						categories.Clear();

						if (ignoreBlockTypes.Contains(b.DefinitionDisplayNameText)) _locked();
						// reactors are locked whenever the reactor manager owns
						// fuel balancing: the sorter must never move fuel in or
						// out of them, or it fights the ReactorMgr average.
						if (MANAGE_REACTORS && b is IMyReactor) _locked();
						// a refinery being used for recipe discovery (RefDiscover)
						// stays locked even if it is renamed mid-discovery. Normal
						// refineries are NOT locked: continuous passive learning
						// was removed, so there are no deltas to protect.
						if (RefDiscover.isDiscovering(b)) _locked();
						// same for an assembler being used for recipe discovery
						// (AsmDiscover): its queue and inventories are ours until
						// the disassembly observation completes.
						if (AsmDiscover.isDiscovering(b)) _locked();
						//if (hiddenBlockTypes.Contains(b.DefinitionDisplayNameText)) _hidden();

						if (!hidden)
						{
							foreach (var tok in t)
							{
								var ltok = tok.ToLower();
								if (ltok.StartsWith("[") && ltok.EndsWith("]"))
								{
									ltok = ltok.Substring(1, ltok.Length - 2);
								}
								if (ltok == "special")
								{
									special = true;
									priority -= 10000;

								}
								else if (ltok == "locked")
								{
									_locked();
								}
								else if (ltok == "hidden")
								{
									_hidden();
								}
								else if (ltok.StartsWith("p"))
								{
									// use ltok (bracket-stripped, lowercased) — tok may still
									// carry the [ ] brackets, which would break the digit check
									var ap = ltok.Substring(1);
									if (ap == "max") priority = int.MinValue;
									else if (ap == "min") priority = int.MaxValue;
									else if (ap.All(char.IsDigit))
									{
										priority -= 10000;
										int c = 0;
										int.TryParse(ap, out c);
										if (c.ToString() == ap)
										{
											priority += c;
										}
									}
								}
								else if (ltok == everything)
								{
									holdall = true;
								}
								else
								{
									foreach (var kvp in cattocargo)
									{
										if (ltok == kvp.Value.ToLower())
										{
											if (!categories.Contains(kvp.Value)) categories.Add(kvp.Value);
											break;
										}
									}
								}
							}

							if (treatBlankAsAlltype && !special && !locked && categories.Count == 0 && !isProduction)
							{
								holdall = true;
								priority += 1;
							}
						}

						if(special)
						{
							holdall = false;
							categories.Clear();
						}
						if(holdall)
						{
							foreach(var kvp in cattocargo)
							{
								if (!categories.Contains(kvp.Value)) categories.Add(kvp.Value);
							}
						}
						if (!special && categories.Count == 0 && APIWC.HasCoreWeapon(b))
						{
							locked = true;
						}

						if (lpriority != priority)
						{
							bPriorityList.Sort();

							var PI = getPI(lpriority);
							PI.bis.Remove(this);
							//PI.update() dead: aggregate typeVolumeFree/categories are write-only
							PI = getPI(priority);
							PI.bis.Add(this);
							//PI.update() dead: aggregate typeVolumeFree/categories are write-only
						}
						if(!special) stocktargets.Clear();
					}
					if (special && b.CustomData != lastCD)
					{
						if (special && b.CustomData == "")
						{
							List<MyItemType> alltypes = new List<MyItemType>();
							List<MyItemType> t = new List<MyItemType>();
							for (var i = 0; i < b.InventoryCount; i++)
							{
								b.GetInventory(i).GetAcceptedItems(t);
								foreach (var e in t) if (!alltypes.Contains(e)) alltypes.Add(e);
							}
							List<string> clinesNZ = new List<string>();
							List<string> clines = new List<string>();
							foreach (var e in alltypes)
							{
								MyFixedPoint amt = 0;
								manifest.stuff.TryGetValue(e, out amt);
								if (amt > 0) clinesNZ.Add(e.TypeId.Substring(bpprefix.Length) + "/" + e.SubtypeId + "=" + amt.ToString());//\n";
								else clines.Add(e.TypeId.Substring(bpprefix.Length) + "/" + e.SubtypeId + "=0");
							}
							clinesNZ.Sort();
							clines.Sort();
							if(clinesNZ.Count == 0)clinesNZ.AddRange(clines);

							b.CustomData = String.Join("\n", clinesNZ);
						}
						if(ISYCOMPAT && b.CustomData.IndexOf("Special Container modes:") == -1)
						{
							b.CustomData = "@Special Container modes:\n- isycompat\n" + b.CustomData;
						}
						lastCD = b.CustomData;
						stocktargets.Clear();
						var lines = lastCD.Split('\n');
						//var newlines = new List<string>();
						foreach (var l in lines)
						{
							//bool kl = true;
							var lr = l.Split('=');
							if (lr.Length == 2)
							{
								var ids = lr[0].Split('/');
								if (ids.Length == 2)
								{
									try
									{
										var t = getType(bpprefix + ids[0], ids[1]);
										if (lr[1] == "all")
										{
											stocktargets[t] = int.MaxValue;
											//if (LOG) log(b.CustomName + " " + t.SubtypeId + "=all");
										}
										else
										{
											var c = (MyFixedPoint)double.Parse(lr[1]);
											if (c > 0)
											{
												stocktargets[t] = c;
												//if (LOG) log(b.CustomName + " " + t.SubtypeId + "=" + c);
											}else
											{
												//kl = false;
											}
										}
									}
									catch (Exception) { }
								}
							}
						}
					}
				}
				public void updateM()
				{
					{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
					InventoryManifest nm = new InventoryManifest();
					if (!hidden) nm.set(this);
					if (manifest == null || !manifest.equals(nm))
					{
						if (manifest != null) Inventory.globalManifest.sub(manifest);
						Inventory.globalManifest.add(nm);
						if (b.CubeGrid.EntityId == gProgram.Me.CubeGrid.EntityId)
						{
							if (manifest != null) Inventory.gridManifest.sub(manifest);
							Inventory.gridManifest.add(nm);
						}
						manifest = nm;
						//getPI(this.priority).update() dead: aggregate typeVolumeFree/categories
						//are write-only; only the .bis membership (maintained in updateP) matters
					}
				}
				public bool updateT()
				{
					//updateT_incomplete = false;
					{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
					if (locked) return false;

					int transfers = transfer_count;

	//IDBG.set(this, null);

					//int MOVES = 0;
					//const int MAX_MOVES = 8;

					//this should actually run always and first, i think
					{
						Dictionary<string, PriorityAggregate> targs = new Dictionary<string, PriorityAggregate>();
						List<MyItemType> keys = new List<MyItemType>(manifest.stuff.Keys);
						//this ensures the dict can be edited during our loop
						//dests we tried to push to this pass that accepted nothing (e.g. no conveyor connection).
						//they are skipped for the rest of this updateT pass so a dead end can't starve
						//other destinations for the category.
						HashSet<BlockInventory> deadEnds = new HashSet<BlockInventory>();

						foreach (var type in keys)//things we have
						{
							{ var _ = gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount ? TripExecution() : false; }
							var cat = cargokeywordbytype(type.TypeId);
							//this should only end up actually calling higherPriorityWithRoomFor once per relevant category tag.
							PriorityAggregate pa = null;
							if (!targs.ContainsKey(cat))
							{
								targs[cat] = pa = higherPriorityWithRoomFor(this, cat);
							}
							else pa = targs[cat];

							int errchk = 0;

							while(pa != null && errchk < 10)//there is a higher priority container in a PriorityAggregate that does want the item's category
							{
								{ var _ = gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount ? TripExecution() : false; }
								MyFixedPoint amt = 0;

								manifest.stuff.TryGetValue(type, out amt);
								if (amt == 0) break;

								MyFixedPoint goalstock = 0;//in case this is a special container, we don't want to push shit we should be keeping
								stocktargets.TryGetValue(type, out goalstock);
								amt -= goalstock;
								if (amt == 0) break;

								errchk++;
								var margin = nonFractionalMinMarginByCat[cat];
								BlockInventory dest = null;
								foreach(var bi in pa.bis)
								{
									if (deadEnds.Contains(bi)) continue;
									if(bi.categories.Contains(cat) && bi.manifest.freeVolume >= margin)
									{//this cargo accepts this category and has more free space than the minimum margin for this category
										dest = bi;
										break;
									}
								}
								if(dest != null)
								{
									//we should start transferring this item.
	//IDBG.set(this, dest);
									//if (amt > dest.manifest.freeVolume)amt = dest.manifest.freeVolume;
									// cap by count that fits, like expel does:
									var nfo = getItemInfo(type);
									MyFixedPoint maxAccept = dest.manifest.freeVolume * (MyFixedPoint)(1.0 / nfo.Volume);
									if (!nfo.UsesFractions) maxAccept = MyFixedPoint.Floor(maxAccept + (MyFixedPoint)0.001);
									if (amt > maxAccept) amt = maxAccept;

									var rem = transfer_item(this, dest, type, amt, false, true);
									if (transfer_count - transfers > MAX_TRANSFERS_PER_OP || transMS > MAX_TRANSFER_MS) return true;

									if (rem > 0)
									{
	//IDBG.log("Unable to xfer " + rem + " of " + type.SubtypeId);
									}
									if (rem == amt)
									{
										//nothing moved: this destination is a dead end for this pass
										//(e.g. no conveyor connection). mark it so it is skipped and
										//the rest of the category isn't starved.
										deadEnds.Add(dest);
									}
									//...the aggregate's typeVolumeFree/categories are write-only
									//(all consumers read bi.categories/bi.manifest.freeVolume
									//directly), so pa.update() here was pure dead work per transfer

									//if we filled the destination beyond nonFractionalMaxMarginByCat,
									//we should delete entry in targs so that higherPriorityWithRoomFor is recomputed for next relevant item
									if (dest.manifest.freeVolume < margin)
									{
										targs[cat] = pa = higherPriorityWithRoomFor(this, cat, deadEnds);
									}
								}
								else
								{
									//no live candidate left in this aggregate (all dead ends or full):
									//fall through to the next lower-priority aggregate that still wants
									//the category, instead of giving up on it.
									targs[cat] = pa = higherPriorityWithRoomFor(this, cat, deadEnds);
								}
								}
							if(errchk == 10)
							{
	//IDBG.log("errchk loop abort");
							}

							{
								if (!categories.Contains(cat)/* && !holdall*/ && !special)
								{
									MyFixedPoint amt = 0;
									
									if(manifest.stuff.TryGetValue(type, out amt) && amt > 0)
									{
										MyFixedPoint goalstock = 0;//in case this is a special container, we don't want to push shit we should be keeping
										stocktargets.TryGetValue(type, out goalstock);
										if (amt > goalstock)
										{
											//nobody higher wants it, but it's not supposed to be in this cargo either.
											//todo: search for equal or lower priority place that wants it. if we can't find one, generate error log message.
											expel(this, type, amt-goalstock);
											if (transfer_count - transfers > MAX_TRANSFERS_PER_OP || transMS > MAX_TRANSFER_MS) return true;
										}
									}
								}
							}
						}
					}

					if (special)
					{
						List<MyItemType> keys = new List<MyItemType>(stocktargets.Keys);
						foreach (var kvp in manifest.stuff) if (!keys.Contains(kvp.Key)) keys.Add(kvp.Key);

						foreach (var type in keys)
						{
							MyFixedPoint curstock = 0;
							manifest.stuff.TryGetValue(type, out curstock);
							MyFixedPoint goalstock = 0;
							stocktargets.TryGetValue(type, out goalstock);
							if (goalstock > curstock && manifest.freeVolume > (MyFixedPoint)getItemInfo(type).Volume)
							{
								MyFixedPoint globalstock = 0;
								globalManifest.stuff.TryGetValue(type, out globalstock);
								if (globalstock > curstock)
								{
	//IDBG.set(type.SubtypeId);
	//IDBG.log(b.CustomName + " globalchk " + type.SubtypeId + " pmove " + goalstock + " " + curstock);
									var r = sort_retrieve(this, type, goalstock - curstock);

									if (transfer_count - transfers > MAX_TRANSFERS_PER_OP || transMS > MAX_TRANSFER_MS) return true;

	//if (r > 0) IDBG.log("unable to satisfy by " + r);
								}
							}
							else if (goalstock < curstock)
							{
	//IDBG.set(type.SubtypeId);
	//IDBG.log("attempt expel " + type.SubtypeId + ": " + goalstock + " < " + curstock + " in " + this.b.CustomName);
								expel(this, type, curstock - goalstock);

								if (transfer_count - transfers > MAX_TRANSFERS_PER_OP || transMS > MAX_TRANSFER_MS) return true;
							}
						}
					}


					return transfer_count - transfers > 0;
				}
			}



			//todo review and update this one
			static public MyFixedPoint sort_retrieve(BlockInventory dest, MyItemType t, MyFixedPoint v, bool sendinputs = false, bool recieveinputs = false)
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
	//IDBG.set(dest, null);
	//IDBG.set(t.SubtypeId);
				var nfo = getItemInfo(t);
				int pidx = BlockInventory.bPriorityList.IndexOf(dest);
				//if (ignorePriorities) pidx = -1;
	//IDBG.log("pidx=" + pidx);
	//IDBG.log("BlockInventory.bPriorityList.Count=" + BlockInventory.bPriorityList.Count);
				for (var i = BlockInventory.bPriorityList.Count - 1; i > pidx; i--)
				{
					{ var _ = gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount ? TripExecution() : false; }
					var inv = BlockInventory.bPriorityList[i];
	//IDBG.set(dest, inv);
					// skip locked sources: a special container's polite stock
					// retrieval must never pull from blocks the sorter has
					// locked (e.g. reactors under ReactorMgr fuel balancing —
					// pulling their fuel would fight the manager). Forced
					// retrieval (force_retrieve) intentionally ignores locks.
					if (inv.locked) continue;
					if (inv.manifest != null && inv.manifest.stuff.ContainsKey(t))
					{

						MyFixedPoint avail = inv.manifest.stuff[t];
	//IDBG.log(inv.b.CustomName + "has item, stock " + avail);
						MyFixedPoint trns_amt = avail > v ? v : avail;
	//IDBG.log("tamt=" + trns_amt);

						MyFixedPoint max_accept = (inv.manifest.freeVolume * (MyFixedPoint)(1 / nfo.Volume));
						if (!nfo.IsOre && !nfo.IsIngot) max_accept = MyFixedPoint.Floor(max_accept + (MyFixedPoint)0.001);
						if (trns_amt > max_accept) trns_amt = max_accept;
	//IDBG.log("tamt_ma=" + trns_amt);
						var rem = transfer_item(inv, dest, t, trns_amt, sendinputs, recieveinputs);
						v -= trns_amt;
						v += rem;
					}
					if (v <= 0) break;
				}
				return v;
			}

			static public MyFixedPoint force_retrieve(BlockInventory dest, MyItemType type, MyFixedPoint amount, bool sendinputs = false, bool recieveinputs = false)
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				foreach (var ibi in Inventory.BlockInventory.bPriorityList)
				{
					MyFixedPoint available = 0;
					ibi.manifest.stuff.TryGetValue(type, out available);
					if (available > 0)
					{
						//var decr = available > amt ? amt : available;
						amount = Inventory.transfer_item(ibi, dest, type, amount, sendinputs, recieveinputs);
						if (amount <= (MyFixedPoint)0.001d) break;
					}
				}
				return amount;
			}

			static public MyFixedPoint expel(BlockInventory origin, MyItemType type, MyFixedPoint amount, bool inputs = false)
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				var nfo = getItemInfo(type);
				var kw = cargokeywordbytype(type.TypeId);
	//IDBG.set(type.SubtypeId);
				for (var i = 0; i < BlockInventory.bPriorityList.Count; i++)
				{
					{ var _ = gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount ? TripExecution() : false; }
					var inv = BlockInventory.bPriorityList[i];
					if (inv != origin && !inv.locked)
					{
	//IDBG.set(origin, inv);
						MyFixedPoint amt = 0;
						MyFixedPoint max_accept = (inv.manifest.freeVolume * (MyFixedPoint)(1 / nfo.Volume));
						if (!nfo.IsOre && !nfo.IsIngot) max_accept = MyFixedPoint.Floor(max_accept + (MyFixedPoint)0.001);

						if (!inv.special && (inv.categories.Contains(kw)/* || inv.holdall*/)) amt = max_accept;
						else if (inv.special)
						{
							MyFixedPoint stock = 0;
							inv.manifest.stuff.TryGetValue(type, out stock);
							MyFixedPoint trg = 0;
							inv.stocktargets.TryGetValue(type, out trg);
							if (trg > stock)
							{
								amt = trg - stock;
								if (amt > max_accept) amt = max_accept;
							}
						}
						if (amt > 0)
						{
	//IDBG.log("maxaccept=" + max_accept);
	//IDBG.log("pushing " + amt + " to " + inv.b.CustomName);
							var remaining = transfer_item(origin, inv, type, amt, inputs, inputs);
							amount -= amt;
							amount += remaining;
						}
					}
					if (amount <= 0) break;
				}
				if (amount > 0)
				{
					StringBuilder err = new StringBuilder();
					err.Append("Warning: failed to expel ").Append(type.SubtypeId).Append(" from \"").Append(origin.lastN).Append("\": nowhere else to store?");
					gInv.rerrlog(err.ToString());
				}
				return amount;
			}

			static public MyFixedPoint transfer_item(BlockInventory origin, BlockInventory dest, MyItemType type, MyFixedPoint amount,
													bool sendinputs = false, bool recieveinputs = false)
			{
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
	//IDBG.set(origin, dest);
	//IDBG.set(type.SubtypeId);
				if (amount == 0) return 0;
	//IDBG.log("transfer_item " + type.SubtypeId + " " + amount+" "+origin.lastN+" > "+dest.lastN);
				//bool cerr = false;
				var sa = amount;
				foreach (var inva in origin.getSortedInventories(sendinputs))
				{
					foreach (var invb in dest.getSortedInventories(recieveinputs))
					{
						amount = transfer_item(inva, invb, type, amount);
						if (amount <= 0 || conveyor_error) break;
					}
					if (amount <= 0 || conveyor_error) break;
				}
				if (conveyor_error)
				{
					conveyor_error = false;
					StringBuilder err = new StringBuilder();
					err.Append("Warning: xfer fail: no conveyor \"").Append(origin.lastN).Append("\" > \"").Append(dest.lastN).Append("\"");
					gInv.rerrlog(err.ToString());
				}
				if (sa != amount)
				{
					//IDBG.log("moved " + (sa - v) + " " + t.SubtypeId + " to " + b.b.CustomName + " from " + a.b.CustomName);
					origin.updateM();
					dest.updateM();
				}
				return amount;
			}

			public static int transfer_count = 0;
			static bool conveyor_error = false;

			static int transTick = 0;
			static double transMS = 0;
			//transfers up to amount of type to dest from origin. returns how much of the amount couldn't be sent for whatever reason
			static public MyFixedPoint transfer_item(IMyInventory origin, IMyInventory dest, MyItemType type, MyFixedPoint amount)
			{
				// Defense-in-depth: the game's conveyor engine can throw on a null endpoint
				// (e.g. a block mid dock/undock) even when both inventories are valid.
				// Any exception must NOT crash the script/run — treat it as "nothing
				// moved" (return the full amount unchanged). PB whitelist: no return
				// inside try/catch, so capture the original and restore on catch.
				MyFixedPoint original = amount;
				try
				{
					if(transTick != tick)
					{
						transTick = tick;
						transMS = 0;
					}
					DateTime s=  DateTime.Now;
					conveyor_error = false;
					List<MyInventoryItem> itms = new List<MyInventoryItem>();
					origin.GetItems(itms);
					foreach (MyInventoryItem item in itms)
					{
						if (item.Type == type)
						{
							MyFixedPoint trns_amt = item.Amount > amount ? amount : item.Amount;
		//IDBG.log("_transfer_item " + type.SubtypeId + " " + trns_amt);
							var nfo = getItemInfo(type);
							MyFixedPoint max_accept = ((dest.MaxVolume - dest.CurrentVolume) * (1f / nfo.Volume));
							if(!nfo.UsesFractions) max_accept = MyFixedPoint.Floor(max_accept + (MyFixedPoint)0.001);

							if (trns_amt > max_accept)
							{
		//IDBG.log("_capping amt to "+max_accept);
								trns_amt = max_accept;
							}
							if (trns_amt > 0)
							{
								transfer_count++;
								if (origin.TransferItemTo(dest, item, trns_amt))
								{
									amount -= trns_amt;
		//IDBG.log("_successfully moved " + trns_amt + " of " + item.Type.SubtypeId);
								}
								else
								{
									bool conveyed = origin.CanTransferItemTo(dest, type);
		//IDBG.log("_failed to move. checkset conveyor flag");
									if (!conveyed) conveyor_error = true;
								}
							}
						}
						if (amount <= 0) break;
					}
					transMS += (DateTime.Now - s).TotalMilliseconds;
				}
				catch (Exception ex)
				{
					// Engine threw (often a null conveyor endpoint on a block being
					// docked/undocked, or a transient game state). Don't crash; behave
					// as if nothing was moved this pass. Retry next tick.
					amount = original;
					string err = "Warning: transfer exception (" + type.SubtypeId + "): " + ex.Message;
					gInv.rerrlog(err.ToString());
				}
				return amount;
			}

			class IDebugger
			{
				bool DEBUG = false;
				string FIRST_CARGO = "";//2 CCTT Cargo Components Ammo P40";
				string SECOND_CARGO = "";//"3 Nascent.Cargo [1.7].[Barge].AllTypes.P70";
				string ITEM = "Stone";// BelterComponent";// LidarComponent";// LithiumCell";
											   //bool retrieve = true;
											   //bool expel = false;

				BlockInventory a = null;
				BlockInventory b = null;
				string curitem = "";
				public void set(BlockInventory a, BlockInventory b)
				{
					this.a = a; this.b = b;
				}
				public void set(string item)
				{
					this.curitem = item;
				}
				public void log(string msg)
				{
					if (!DEBUG) return;
					bool l = true;
					if (FIRST_CARGO.Length > 0 && (a == null || a.lastN != FIRST_CARGO)) l = false;
					if (l && SECOND_CARGO.Length > 0 && (b == null || b.lastN != SECOND_CARGO)) l = false;
					if (l && ITEM.Length > 0 && curitem != ITEM) l = false;
					if (l)
					{
						Program.log(msg);
					}
				}
			}
	//static IDebugger IDBG = new IDebugger();


			public void updateContainers(List<IMyTerminalBlock> c)
			{

				List<IMyTerminalBlock> del = new List<IMyTerminalBlock>();
				foreach (var b in containers)
				{
					if (!c.Contains(b)) del.Add(b);
				}
				InventoryManifest dMan = new InventoryManifest();
				foreach (var b in del)
				{
					BlockInventory bi = BlockInventory.getBI(b);
					Inventory.globalManifest.sub(bi.manifest);
					if (b.CubeGrid.EntityId == gProgram.Me.CubeGrid.EntityId)
						Inventory.gridManifest.sub(bi.manifest);
				}
				containers.Clear();


				foreach (var e in c) if (e.CubeGrid.EntityId != gProgram.Me.CubeGrid.EntityId) containers.Add(e);
				foreach (var e in c) if (e.CubeGrid.EntityId == gProgram.Me.CubeGrid.EntityId) containers.Add(e);

				upd();
			}
			public List<IMyTerminalBlock> containers = new List<IMyTerminalBlock>();
			public int nextC = 0;
			int nextCS = 0;

			bool itemsUpdating = true;
			public bool hasUpdatedOnce = false;
			public int updateInterval = 1;//60 * 10;

			public int lastUpdateTick = 0;
			public int ticksRun = 0;

			//static Profiler invuP = new Profiler("invu");

			public int updateCounter = 0;

			public enum STATUS
			{
				PREINIT,
				INIT,
				MANIFESTS,
				IDLE
			}
			public string[] statlbl = {
				"PREINIT",
				"INIT",
				"PROCESSING",
				"IDLE"
			};
			public STATUS cstat = STATUS.PREINIT;
			public Queue<string> errors = new Queue<string>();
			public int rerrtick = 0;
			public bool errd = false;
			public void rerrlog(string s)
			{
				errd = true;
				errors.Enqueue(s);
				if (errors.Count > 5) errors.Dequeue();
				rerrtick = tick;
			}
			//static Profiler statP = new Profiler("stat");
			public string lastStatus = "";
			// status text generation moved out of Inventory into Program.genStatus()
			// (StatusGen.cs) - this class owns the inventory state, the standalone
			// function retrieves what it needs from the managers instead.
			public void genstatus()
			{
				gProgram.genStatus();
			}


			void clr()
			{
				itemsUpdating = false;
				ticksRun = tick - lastUpdateTick;
				lastUpdateTick = tick;
				nextC = 0;
				cstat = STATUS.IDLE;
			}
			void upd()
			{
				cstat = STATUS.INIT;

				itemsUpdating = true;
				lastUpdateTick = tick;
				nextC = 0;
			}

			bool movedItems = false;
			int lastBlockUpdate = 0;
			int blockUpdateStep = 0;
			int SANchk = 0;

			//static Profiler cdbgP = new Profiler("cdbg");

			public void update()
			{
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.PassStart) : false; }
				//cdbgP.s();
				{ var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				if (!itemsUpdating && (tick - lastUpdateTick > updateInterval))
				{
					upd();

					if (CARGODBG && cargodbg != null)
					{
						//	genstatus();
						//string o = "";
						StringBuilder fml = new StringBuilder();
						foreach (var e in BlockInventory.bPriorityList)
						{
							fml.Append(e.priority + "|||" + e.b.CustomName + "\n");
						}
						cargodbg.WriteText(fml.ToString());
					}
				}
				//cdbgP.e();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.PassStart) : false; }
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.InvBlocks) : false; }
				//invuP.s();
				if (itemsUpdating)
				{
					if (nextC >= containers.Count)
					{
						clr();
						log("full run (" + containers.Count + "):" + ticksRun + "t (" + (ticksRun / 60.0d).ToString("0.0") + "s");
						hasUpdatedOnce = true;
						updateCounter += 1;
					}
					else
					{
						var intrvl = blockInterval;
						if (movedItems) intrvl = blockIntervalMove;

						if (tick - lastBlockUpdate > intrvl)
						{
							IMyTerminalBlock t = containers[nextC];
							BlockInventory bi = BlockInventory.getBI(t);
							var bus = blockUpdateStep;
							blockUpdateStep++;
							if (bus == 0) { { var _ = DEBUGGING ? diag.Enter(DbgLabel.InvManifest) : false; } bi.updateM(); { var _ = DEBUGGING ? diag.Exit(DbgLabel.InvManifest) : false; } }
							if (bus == 1) { { var _ = DEBUGGING ? diag.Enter(DbgLabel.InvPriority) : false; } bi.updateP(); { var _ = DEBUGGING ? diag.Exit(DbgLabel.InvPriority) : false; } }
							if (bus == 2)
							{
								if (SORT)
								{
									{ var _ = DEBUGGING ? diag.Enter(DbgLabel.InvTransfer) : false; }
									movedItems = bi.updateT();
									{ var _ = DEBUGGING ? diag.Exit(DbgLabel.InvTransfer) : false; }
									SANchk++;
								}
								if (!movedItems || SANchk > 10)
								{
									SANchk = 0;
									lastBlockUpdate = tick;
									blockUpdateStep = 0;
									nextC++;
								}
								else
								{
									lastBlockUpdate = tick;
									blockUpdateStep--;
								}
							}
							cstat = STATUS.MANIFESTS;
						}
					}
				}
				//invuP.e();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.InvBlocks) : false; }
				genstatus();
			}
		}
	}
}
