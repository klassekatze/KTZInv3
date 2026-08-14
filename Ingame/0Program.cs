using Microsoft.Win32;
using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{ 
	public partial class Program : MyGridProgram
	{
		#region mdk preserve
		static public double maxMsPerSETickReal = 0.05;

		static bool USE_SKITS = false;//whether to use survival kits in autocrafting
		static bool ASSEMBLE = true;//auto assemble items to the quota dictated by Autocraft LCD
		static bool DISASSEMBLE = true;//auto disassemble items in excess of quota
		static bool PRIORITY_DISASSEMBLE = true;// if true, disassemble then assemble. else assemble then disassemble. assemble first will "jam" if lacking materials and never disassemble
		static int ASSEMBLE_MARGIN = 50;//margin of error around quota before doing any of that

		static int REACTOR_BALANCING_MARGIN = 25;//moving reactor fuel causes power fluctuations, so we don't do it unless beyond this imbalance.
		static bool MANAGE_REACTORS = true;//rebalance fuel across reactors (ReactorMgr) AND lock reactors in the sorter so
											//it never moves fuel in/out of them. When false the reactor manager is disabled
											//and reactors are sorted like any other container.
		static bool REFINERY_LEARN = true;//enable isolated refinery recipe discovery (RefDiscover): every
										//second the controller scans for an ENABLED refinery that accepts
										//an ore we don't know the recipe for (with >= 3000 in stock) and
										//preempts it - the refinery is isolated (conveyors off, locked from
										//the sorter, flushed), stuffed with the unknown ore, and observed
										//until the conversion is learned. Known recipes never block the
										//scan: the first unknown (refinery type, ore) pair wins, one at a
										//time. There is no continuous passive refinery learning - isolated
										//discovery windows are the only ones trusted. When false the
										//discovery controller is disabled.
		static bool ASM_DISCOVER = true;//enable isolated assembler recipe discovery (AsmDiscover): when we know
										//an autocraft blueprint for an item and possess at least one copy of the
										//item itself, an assembler is isolated (conveyors off, locked from the
										//sorter, flushed) and the item is disassembled to learn the exact
										//composition of the recipe, which is saved to the CD registry. When
										//false the discovery controller is disabled.
		static public bool WARMUP_JIT = true;//call the real tick body several times at the end of
										//Program() so the JIT compiles the hot paths during
										//construction - PBLimiter's per-tick budget does not apply
										//to the ctor, so the compile stall is paid once at boot
										//instead of on the first real tick that reaches each path.
										//The instruction counter DOES run in the ctor: the warmup
										//runs the real tick body up to JIT_WARMUP_TICKS times, each
										//bounded by the same MaxInstructionCount guard the ticks
										//use, so it stops when the ctor budget is spent. When false
										//the warmup is skipped. Public so the test harness can
										//disable it for deterministic per-test boot state.
		static int JIT_WARMUP_TICKS = 200;//how many real tick bodies the ctor JIT warmup runs.
										//Boot alone needs ~110 ticks (11 steps at tick%10), and
										//after boot the inventory passes + manager updates are the
										//heavy JIT surface - so 200 gets through boot AND into the
										//real passes. The MaxInstructionCount check caps it in-game;
										//in the harness the fake Runtime reports 0 instructions so
										//the cap is the only bound (tests disable WARMUP_JIT anyway).
										//The runtime-ms skip check is bypassed while warmup runs.
		static bool JITWARMUP = false;//warmup mode: (a) disables the runtime-ms skip check
										//(SkipCheck) so ctor ticks neither skip work nor pollute the
										//rolling average that real ticks are judged against, and
										//(b) stops the SLEEP counter (_ticks) from advancing - the
										//warmup ticks are real work on `tick` but NOT scheduled
										//ticks, so they must not shift the skip guard's window or
										//hangTick debt. Set true by jitWarmup(), restored in finally.

		//static bool ASM_FLUSH = false;//whether to periodically clear inputs of an assembler that is not producing
		static bool ASM_SHUFFLE = false;//whether to periodically move the first item to back of queue if not producing

		static bool MAKE_CONDUIT_PACKET = true;// whether to write a Conduit packet to CD of block name with Conduit.

		//static bool NEVER_SORT_REFINERIES = false;
		//static bool NEVER_SORT_REACTORS = true;//sorting reactors can reset their work state

		static string AUTOCRAFT_LCD_KEYWORD = "KTZ Autocraft";

		public List<string> orePriorityOrder = new List<string> {
		"Stone",
		"Iron",
				"Uranium",//for fusion fuel
		"Scrap",
		"Gold",
		"Magnesium",
		"Silicon",
		"Nickel",
		"Silver",
		"Copper",
		"Titanium",
		"Platinum",
		"Tungsten",
		"Cobalt",//
		"Lead",//
		};

		static int connectEventInterval = 10;
		static UpdateFrequency updateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update10 | UpdateFrequency.Update100;


		static int MAX_TRANSFERS_PER_OP = 4;
		//moving items is fundamentally expensive, moreso that it seems. this limits the amount of actual stacks moved in a given operation.
		//Lower numbers make performance more consistent and thus reliable with things like pblimiter, but
		//can result in it taking several passes over the grid to get things to their final destinations if things are sufficiently disordered.
		static double MAX_TRANSFER_MS = 0.05;//secondary sancheck

		static bool SORT = true;//whether to do any of that item sorting stuff at all.
								//Note: Even if sorting isn't otherwise used, input flushing of jammed assemblers won't
								//happen without a tagged cargo for Ingots, Components
		static bool MERGE_STACKS = true;
		//whether to merge multiple stacks of the same itemtype in a given container when possible
		//could cause desync, they say? idk. stacks should only be fragmented like this by player action

		static bool ISYCOMPAT = true;

		//these are blocks by DefinitionDisplayNameText
		//containers that cannot or should not be managed, ever, typically due to lack of conveyor support
		static string[] ignoreBlockTypes = {
			"Cargo Crate",
			"Lockers",
			"Armory Lockers",
			"Armory",
			"Weapon Rack",
			"Control Seat",
			"Parachute Hatch",
			"Control Station",
			"Vending Machine"
		};

		//PERFORMANCE CONTROLS
		static bool CARGODBG = false;
		static public int blockInterval = 5;//ticks to wait for next block scan when nothing has been moved
		static public int blockIntervalMove = 15;//ticks to wait if the last op required moving items around
		#endregion


		static public Program gProgram = null;
		static public DateTime bootTime;
		public Program()
		{
			gProgram = this;
			resourceLoader = new ResourceLoader();
			resourceLoader.p = this;
			bootTime = DateTime.Now;

			// budget guard: trip at 90% of the engine's per-run limits so we can
			// abort a runaway tick cleanly (throw + catch in Main) instead of the
			// engine killing the script (ScriptOutOfInstructionsException etc.)
			// Runtime is wired onto the instance BEFORE the ctor runs (the game
			// uses FormatterServices.GetUninitializedObject + property set).
			// Static de-facto consts so the nested managers can read them.
			MaxInstructionCount = Runtime.MaxInstructionCount * 9 / 10;
			MaxCallChainDepth = Runtime.MaxCallChainDepth * 9 / 10;

			log("BOOT", LT.LOG_N);
			//Config = new Config_();
			Runtime.UpdateFrequency = updateFrequency;// UpdateFrequency.Update1;//| */UpdateFrequency.Update10 | UpdateFrequency.Update100;

			jitWarmup();
		}

		// ============================================================================
		// JIT warmup.
		//
		// A freshly compiled script pays a JIT stall on the FIRST invocation of
		// every method: the first real ticks after boot hit the compiler
		// synchronously (JIT compiles a method's ENTIRE body on first call),
		// causing visible hitches. The fix: run the REAL tick body a few times
		// during the constructor, where PBLimiter's per-tick wall-clock budget
		// does NOT apply (the ctor is not a scheduled run). The instruction
		// counter DOES run in the ctor, so the warmup loop is bounded by the
		// same MaxInstructionCount guard the ticks use - it simply stops when
		// the budget for the ctor run is spent.
		//
		// WHY CALL MAIN() INSTEAD OF INDIVIDUAL METHODS: the per-tick paths are
		// gated by conditionals (tick % 60, gInv.hasUpdatedOnce, refi counters,
		// discovery state). Calling update() once would hit the gate and return,
		// and a method that returns early never CALLS its callees, so those stay
		// uncompiled. Running main() for real walks the actual gates: each
		// tick advances the state machine (resourceLoader boot steps, inventory
		// passes, manager updates), so by the second or third warmup tick the
		// real hot paths have executed - and the JIT has compiled them and
		// everything they call. Real work on real paths, no enumeration.
		//
		// The runtime-ms skip check (SkipCheck) is disabled during warmup:
		// (a) the ctor is not a scheduled tick, so LastRunTimeMs is meaningless
		// there, and (b) warmup ticks must not pollute the rolling average that
		// the real ticks will be judged against.
		// ============================================================================
		static void jitWarmup()
		{
			if (!WARMUP_JIT) return;
			JITWARMUP = true;
			try
			{
				for (int i = 0; i < JIT_WARMUP_TICKS; i++)
				{
					// same budget guard the real ticks use: stop when the
					// ctor's instruction budget is exhausted
					if (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount) break;
					gProgram.Main("", UpdateType.Update1);
				}
			}
			finally
			{
				JITWARMUP = false;
			}
		}

		// ---- budget trip guard ----
		// The engine kills the script when the injected instruction counter
		// exceeds Runtime.MaxInstructionCount (50000) or the call chain depth
		// exceeds Runtime.MaxCallChainDepth (1000). Trip at 90% of those with a
		// script-defined exception that Main catches silently: the tick's work is
		// abandoned cleanly, the run block unwinds (ExitMethod runs in the
		// injected finallys), and the next tick starts with fresh counters.
		public static int MaxInstructionCount = 0;
		public static int MaxCallChainDepth = 0;

		/// <summary>Script-defined exception type (whitelist IsInSource) used to
		/// abort a tick that is approaching the engine's run budget.</summary>
		public class ExecutionTripException : Exception
		{
		}

		/// <summary>Throws <see cref="ExecutionTripException"/>. Must return bool
		/// so it fits the ternary guard expression.</summary>
		public static bool TripExecution()
		{
			throw new ExecutionTripException();
		}

		// NO TripGuard()/TripGuardInstr() helper methods: any method call gets the
		// rewriter's full injected wrap (EnterMethod/CountInstructions/ExitMethod)
		// on EVERY evaluation - burning the budget the guard protects and briefly
		// raising the call chain depth. The guard must be an INLINE TERNARY at each
		// site (expression -> never wrapped by InjectedBlock):
		//   entry:    { var _ = (gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount || gProgram.Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
		//   loophead: { var _ = gProgram.Runtime.CurrentInstructionCount > MaxInstructionCount ? TripExecution() : false; }

		public void Save()
		{
			// Called when the program needs to save its state. Use
			// this method to save your state to the Storage field
			// or some other means. 
			// 
			// This method is optional and can be removed if not
			// needed.

			/*string donor_name = "BLOCK_TO_COPY";
			IMyConveyorSorter donor = null;
			List<IMyConveyorSorter> blocks = new List<IMyConveyorSorter>();
			List<IMyConveyorSorter> recipients = new List<IMyConveyorSorter>();
			GridTerminalSystem.GetBlocksOfType(blocks);
			for(int i = 0; i < blocks.Count; i++)
			{
				var b = blocks[i];
				if (b.CustomName == donor_name) donor = b;
				else recipients.Add(b);
			}
			if (donor != null)
			{
				List<MyInventoryItemFilter> filters = new List<MyInventoryItemFilter>();
				donor.GetFilterList(filters);
				var mode = donor.Mode;
				for (int i = 0; i < recipients.Count; i++)
				{
					var b = recipients[i];
					b.SetFilter(mode, filters);
				}
				Echo("copied donor to "+ recipients.Count);
			}
			else Echo("donor not found");*/
		}
		public static int tick = -1;
		//static BurnoutTrack bt60 = new BurnoutTrack(60, maxScriptTimeMSPerSec);

		//static Profiler initP = new Profiler("init");
		//static Profiler mainP = new Profiler("main");

		int uf1 = 0;
		int uf10 = 0;
		int uf100 = 0;

		double tbtticks = 0;
		DateTime ltickt = DateTime.Now;

		//#region premain
		//public void Main(string arg, UpdateType upd)
		//{
		//	tick += 1;
		/*DateTime ntick = DateTime.Now;
		if(tick > 1)tbtticks += (ntick- ltickt).TotalMilliseconds;
		ltickt = ntick;
		//else if (updateFrequency == UpdateFrequency.Update10) tick += 10;
		//else if (updateFrequency == UpdateFrequency.Update100) tick += 100;

		if ((upd & UpdateType.Update1) != 0) uf1++;
		if ((upd & UpdateType.Update10) != 0) uf10++;
		if ((upd & UpdateType.Update100) != 0) uf100++;*/


		/*Echo(tick + "");
		Echo(uf1 + "");
		Echo(uf10 + "");
		Echo(uf100 + "");
		Echo((tbtticks/ tick)+"");*/
		//Echo((upd & UpdateType.Update1) != 0 ? "Update1" : "");
		//Echo((upd & UpdateType.Update10) != 0 ? "Update10" : "");
		//Echo((upd & UpdateType.Update100) != 0 ? "Update100" : "");

		//#region burnoutfailsafepre
		//if (bt60.burnoutpre()) return;
		//#endregion
		
		//	#region burnoutfailsafepost
		//	if (bt60.burnoutpost()) return;
		//	#endregion
		//}

		//#endregion

		static Inventory gInv = null;
		static Autocraft gAutocraft = null;
		static AssemblerMgr gAssemblerMgr = null;
		static RefineryMgr gRefineryMgr = null;
		static RefDiscover gRefDiscover = null;
		static AsmDiscover gAsmDiscover = null;
		static ReactorMgr gReactorMgr = null;

		public string mainArg = "";
		public UpdateType updateType = UpdateType.Update1;
		public void Main(string arg, UpdateType upd)
		{
			mainArg = arg;
			updateType = upd;
			try
			{
				{ var _ = (Runtime.CurrentInstructionCount > MaxInstructionCount || Runtime.CurrentCallChainDepth > MaxCallChainDepth) ? TripExecution() : false; }
				bool skip = skipper;
			}
			catch (ExecutionTripException)
			{
				// budget guard tripped: abandon this tick's work cleanly. The
				// engine's injected try/finally ran ExitMethod during unwinding,
				// so the call chain depth is balanced; next tick starts fresh.
			}
		}

		static private readonly int _windowSize = 60;
		private readonly double[] _history = new double[_windowSize];
		private double _runningSum = 0;
		int recorded = 0;
		int hangTick = -1;
		bool skipRecord = true;
		bool SkipCheck//returns true for any tick that should be skipped
		{
			get
			{
				// 1. ALWAYS LOG REALITY FIRST
				// No matter what happened last tick (heavy run or instant skip), 
				// log the exact truth reported by the game engine right now.
				double l_time = Runtime.LastRunTimeMs;
				if (JITWARMUP) return false; // ctor warmup: not a scheduled tick -
											 // do not record into the rolling
											 // average and never skip the warmup
											 // ticks (their LastRunTimeMs is
											 // meaningless; polluting the window
											 // would trip the guard for the real
											 // ticks that follow)
				if (skipRecord)
				{
					int index = _ticks % _windowSize;

					_runningSum -= _history[index];
					_history[index] = l_time;
					_runningSum += l_time;

					if (recorded < _windowSize) recorded++;
				}

				// 2. NOW CHECK IF WE ARE SERVING A DEBT
				// If we are currently supposed to be skipping, we return true here.
				// The truth (0ms) of this skip will be caught and logged on the NEXT tick.
				if (_ticks < hangTick) return true;

				// 3. EVALUATE THE MATHEMATICAL AVERAGE
				double currentAverage = _runningSum / recorded;

				var limit = maxMsPerSETickReal;
				if (currentAverage > limit)
				{
					int requiredTicks = (int)Math.Ceiling((_runningSum / limit) - recorded);
					if (requiredTicks > 0)
					{
						hangTick = _ticks + requiredTicks;
						reportLastRun = true;
						log(currentAverage.ToString("0.00") + "ms > " + limit.ToString("0.00") + "ms in tick " + _ticks + "; skipping " + requiredTicks + "t (" + (((double)requiredTicks) / 60).ToString("0.0") + "s)");
					}
				}
				return false;
			}
		}
		bool reportLastRun = false;
		bool skipper
		{
			get
			{
				// _ticks is the SLEEP counter: it advances on every scheduled
				// Update1 call, even ticks that SkipCheck then skips. The rest
				// of the program's work cadence must NOT use it (see ReactorMgr
				// and Conduit, which gate on `tick` - executed ticks only).
				// During the ctor JIT warmup we run real tick bodies but those
				// are not scheduled ticks: do not advance the sleep counter, so
				// warmup can never shift the skip guard's rolling window or
				// hangTick debt.
				if (updateType == UpdateType.Update1 && !JITWARMUP) _ticks++;
				//startTicks = DateTime.UtcNow.Ticks;
				//startTime = DateTime.Now;
				if (reportLastRun)
				{
					log("Runtime.LastRunTimeMs=" + Runtime.LastRunTimeMs.ToString("0.00"));
					reportLastRun = false;
				}

				var x = (_ticks != 0 && SkipCheck) ? false : hypermain;

				if (_ticks % 30 == 0)
				{
					Echo("t:" + _ticks);
					if (_ticks < hangTick)
					{
						Echo("rt:" + (_ticks - hangTick));
					}
				}
				//var l_time = Runtime.LastRunTimeMs;
				//var _ = hypermain;
				return true;
			}
		}

		public static int _ticks = 0;
		public bool hypermain
		{
			get
			{
				tick += 1;
				if (tick % 20 == 0) if (Me.Closed)
				{
					Runtime.UpdateFrequency = UpdateFrequency.None;
					return true;
				}
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.Main) : false; }
				//mainP.start();
				main(mainArg, updateType);
				//mainP.stop();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.Main) : false; }
				if (tick % 5 == 0)
				{
					Echo(tick.ToString());
					//if (profileLog != null) profileLog.WriteText("name:ms1t:ms60t\n" + Profiler.getAllReports());
					if (gInv != null)
					{
						Echo(gInv.lastStatus);
					}
				}
				if (consoleLog != null && tick % 5 == 0)
				{
					if (Logger.loggedMessagesDirty)
					{
						Logger.updateLoggedMessagesRender();
						consoleLog.WriteText(Logger.loggedMessagesRender);
					}
				}
				
				return true;
			}
		}


				bool first = true;
		void main(string arg, UpdateType upd)
		{
			{ var _ = DEBUGGING ? diag.Enter(DbgLabel.Init) : false; }
			//initP.start();
			if (tick % 10 == 0)
			{
				resourceLoader.update();
			}
			//initP.stop();
			{ var _ = DEBUGGING ? diag.Exit(DbgLabel.Init) : false; }
			if (resourceLoader.neverFullyLoaded)
			{
				Echo("INITIALIZING: " + resourceLoader.step + "/11");
				if (statusLog != null) statusLog.WriteText("INIT: " + resourceLoader.step + "/11");
				return;
			}
			if (first)
			{
				first = false;

				gInv = new Inventory();
				gInv.updateContainers(inventoryBlocks);
				gAssemblerMgr = new AssemblerMgr();
				gRefineryMgr = new RefineryMgr();
				gRefDiscover = new RefDiscover();
				gAsmDiscover = new AsmDiscover();
				gAutocraft = new Autocraft();
				gReactorMgr = new ReactorMgr();
				log("Basic structures initialized.");
				//bt60.setwait(60);
			}
			{ var _ = DEBUGGING ? diag.Enter(DbgLabel.ConnectEvents) : false; }
			if (tick % connectEventInterval == 0) connectEvent2();
			{ var _ = DEBUGGING ? diag.Exit(DbgLabel.ConnectEvents) : false; }
			if (SLEEPING) return;


			if (autocraftingLCD != null)
			{
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.AsmMgr) : false; }
				gAssemblerMgr.update();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.AsmMgr) : false; }
			}
			{ var _ = DEBUGGING ? diag.Enter(DbgLabel.Refinery) : false; }
			gRefineryMgr.update();
			{ var _ = DEBUGGING ? diag.Exit(DbgLabel.Refinery) : false; }
			{ var _ = DEBUGGING ? diag.Enter(DbgLabel.RefDiscover) : false; }
			gRefDiscover.update();
			{ var _ = DEBUGGING ? diag.Exit(DbgLabel.RefDiscover) : false; }
			{ var _ = DEBUGGING ? diag.Enter(DbgLabel.AsmDiscover) : false; }
			gAsmDiscover.update();
			{ var _ = DEBUGGING ? diag.Exit(DbgLabel.AsmDiscover) : false; }
			{ var _ = DEBUGGING ? diag.Enter(DbgLabel.Reactor) : false; }
			gReactorMgr.update();
			{ var _ = DEBUGGING ? diag.Exit(DbgLabel.Reactor) : false; }
			{ var _ = DEBUGGING ? diag.Enter(DbgLabel.Conduit) : false; }
			conduitUpdate();
			{ var _ = DEBUGGING ? diag.Exit(DbgLabel.Conduit) : false; }

			gInv.update();
			if (tick % (60 * 5) == 0)
			{
				//if (statusLog != null) statusLog.WriteText(invInterface.listInv());

				if (autocraftingLCD != null)
				{
					{ var _ = DEBUGGING ? diag.Enter(DbgLabel.LcdRead) : false; }
					//aclcd.s();
					var txt = autocraftingLCD.GetText();
					//if(txt.StartsWith(""))
					gAutocraft.readLCD(txt);
					//aclcd.e();
					{ var _ = DEBUGGING ? diag.Exit(DbgLabel.LcdRead) : false; }
					{ var _ = DEBUGGING ? diag.Enter(DbgLabel.LcdWrite) : false; }
					//aclcd2.s();
					var o = gAutocraft.writeLCD();
					autocraftingLCD.WriteText(o);
					//aclcd2.e();
					{ var _ = DEBUGGING ? diag.Exit(DbgLabel.LcdWrite) : false; }
				}
			}
			if (arg == "clearasm")
			{
				foreach(var asm in assemblers)
				{
					asm.ClearQueue();
				}
			}
			if (arg == "test")
			{
				var asm = assemblers[0];
				var bp = Autocraft.blueprints.First().Value;



				List<MyProductionItem> pro = new List<MyProductionItem>();
				asm.GetQueue(pro);
				if (pro.Count == 0)
				{
					asm.AddQueueItem(bp, (MyFixedPoint)1000);
				}
				else
				{
					var e = pro[0];
					asm.InsertQueueItem(0, bp, (MyFixedPoint)(-500));
				}
			}
			else if (arg == "log")
			{
				Logger.writeSuperlog();
			}
		}
		//static Profiler aclcd = new Profiler("aclcd1");
		//static Profiler aclcd2 = new Profiler("aclcd2");

		bool SLEEPING = false;

		static string nosort = "No Sorting";
		static string nosort2 = "No IIM";
		class ConnectorInfo
		{
			bool lcon = false;
			public IMyShipConnector connector = null;
			public IMyShipConnector otherConnector = null;

			public bool sortConnected = false;
			//public bool blockConnected = false;

			public bool upd()
			{
				var con = connector.Status == MyShipConnectorStatus.Connected;
				var o = connector.OtherConnector;
				if (lcon != con)
				{
					lcon = con;
					if (!con) o = null;
					otherConnector = o;
					sortConnected = false;
					if (otherConnector != null)
					{
						var n = connector.CustomName;
						var n2 = otherConnector.CustomName;
						if (n.Contains(nosort) || n.Contains(nosort2) ||
						n2.Contains(nosort) || n2.Contains(nosort2))
						{
							sortConnected = false;
						}
						else sortConnected = true;
					}
					return true;
				}
				else return false;
			}

		}
		Dictionary<IMyShipConnector, ConnectorInfo> connectorState = new Dictionary<IMyShipConnector, ConnectorInfo>();

		Dictionary<IMyCubeGrid, IMyShipConnector> getCgridmap = new Dictionary<IMyCubeGrid, IMyShipConnector>();
		List<IMyCubeGrid> getCbl = new List<IMyCubeGrid>();
		int ltick = -1;
		IMyShipConnector getRelevantConnector(IMyTerminalBlock b)
		{
			if (ltick != tick)
			{
				getCgridmap.Clear();
				getCbl.Clear();
				foreach (var c in connectors)
				{
					var o = c.OtherConnector;
					if (o != null) getCgridmap[c.OtherConnector.CubeGrid] = c;
				}
			}
			IMyShipConnector match = null;
			getCgridmap.TryGetValue(b.CubeGrid, out match);
			if (match != null) return match;
			else
			{
				if (!getCbl.Contains(b.CubeGrid))
				{
					foreach (var kvp in connectorState)
					{
						var v = kvp.Value; ;
						if (v.otherConnector != null && v.otherConnector.CubeGrid == b.CubeGrid)
						{
							if (v.sortConnected) return getCgridmap[b.CubeGrid] = v.connector;
							else
							{
								getCbl.Add(b.CubeGrid);
								return null;
							}
						}
					}
					foreach (var kvp in connectorState)
					{
						var v = kvp.Value; ;
						if (v.otherConnector != null && v.sortConnected)
						{
							if (b.IsSameConstructAs(v.otherConnector))
							{
								return getCgridmap[b.CubeGrid] = v.connector;
							}
						}
					}
				}
				else return null;
			}
			getCbl.Add(b.CubeGrid);
			return null;
		}


		public bool updateConnectors()
		{
			bool evnt = false;
			foreach (var c in connectors)
			{
				ConnectorInfo v = null;
				connectorState.TryGetValue(c, out v);
				if (v == null)
				{
					v = new ConnectorInfo();
					v.connector = c;
					connectorState[c] = v;
				}
				evnt = v.upd() || evnt;
			}
			return evnt;
		}
		public void recalcInvBlocks()
		{
			List<IMyTerminalBlock> blox = new List<IMyTerminalBlock>();
			GridTerminalSystem.GetBlocksOfType(blox);
			inventoryBlocks.Clear();
			List<IMyCubeGrid> subGrids = new List<IMyCubeGrid>();
			List<IMyCubeGrid> notSubGrids = new List<IMyCubeGrid>();
			foreach (var b in blox)
			{
				if (b.HasInventory && b.HasPlayerAccess(Me.OwnerId))
				{
					if (b.CubeGrid == Me.CubeGrid) inventoryBlocks.Add(b);
					else
					{
						if (!notSubGrids.Contains(b.CubeGrid))
						{
							if (b.IsSameConstructAs(Me)) subGrids.Add(b.CubeGrid);
							else notSubGrids.Add(b.CubeGrid);
						}
						if (subGrids.Contains(b.CubeGrid)) inventoryBlocks.Add(b);
						else
						{
							var rc = getRelevantConnector(b);
							if (rc != null)
							{
								ConnectorInfo v = null;
								connectorState.TryGetValue(rc, out v);
								if (v != null && v.sortConnected)
								{
									inventoryBlocks.Add(b);
								}
							}
						}
					}
				}
			}
			gInv.updateContainers(inventoryBlocks);
		}

		bool cnctE = false;
		public void connectEvent2()
		{
			try
			{
				bool evnt = updateConnectors();

				if (evnt)
				{

					log("connector change");
					List<IMyProgrammableBlock> pgms = new List<IMyProgrammableBlock>();
					GridTerminalSystem.GetBlocksOfType(pgms, b => b != Me && b.HasPlayerAccess(Me.OwnerId) && b.CustomData.StartsWith("KTZINV") && b.IsWorking);
					bool awake = true;
					if (pgms.Count > 0)
					{
						bool stati = Me.CubeGrid.IsStatic;
						foreach (var c in pgms)
						{
							if (c.CubeGrid.IsStatic && !stati)
							{
								awake = false;
								break;
							}
						}
						if (awake)
						{
							foreach (var c in pgms)
							{
								if (c.EntityId < Me.EntityId && (!stati || c.CubeGrid.IsStatic))
								{
									awake = false;
									break;
								}
							}
						}
					}
					SLEEPING = !awake;
					if (SLEEPING)
					{
						gInv.lastStatus = "Sleeping while a connected KTZInv runs.";
						if (statusLog != null) statusLog.WriteText(gInv.lastStatus);
						return;
					}

					recalcInvBlocks();
				}
			}
			catch (Exception e)//because torch or something i forget
			{
				if (!cnctE)
				{
					cnctE = true;
					log("Exception: " + e.ToString());
				}
			}
		}
	}
}
