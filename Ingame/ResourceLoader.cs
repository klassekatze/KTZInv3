using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage.Utils;
using VRageMath;

namespace IngameScript
{
	partial class Program : MyGridProgram
	{
		static IMyTextSurface consoleLog = null;
		static IMyTextSurface statusLog = null;
		static IMyTextSurface profileLog = null;
		static IMyTextSurface cargodbg = null;
		static IMyTerminalBlock conduit = null;

		static IMyTextSurface autocraftingLCD = null;

		static List<IMyAssembler> assemblers = new List<IMyAssembler>();
		static List<IMyRefinery> refineries = new List<IMyRefinery>();
		static List<IMyReactor> reactors = new List<IMyReactor>();


		//static List<IMyTerminalBlock> weaponCoreWeapons = new List<IMyTerminalBlock>();
		static List<IMyShipController> controllers = new List<IMyShipController>();
		static List<IMyShipConnector> connectors = new List<IMyShipConnector>();
		static List<IMyTerminalBlock> inventoryBlocks = new List<IMyTerminalBlock>();
		static public WcPbApi APIWC = null;
		static public ResourceLoader resourceLoader = null;
		public class ResourceLoader
		{
			public Program p;

			public bool neverFullyLoaded = true;
			public ResourceLoader()
			{
				mkBlockCheckMachine();
			}

			bool readConfig = false;

			public void update()
			{
				if (APIWC == null)
				{
					APIWC = new WcPbApi();
					try
					{
						APIWC.Activate(gProgram.Me);
					}
					catch (Exception) { }
					
				}
				if (!APIWC.isReady && tick % 30 == 0)
				{
					try
					{
						APIWC.Activate(gProgram.Me);
					}
					catch (Exception) { }
				}
				if (!APIWC.isReady) return;

				if (!readConfig || tick % 60 == 0)
				{
					readConfig = true;
					/*if (p.Me.CustomData != lastCustomData)
					{
						//log("Loading CustomData.", LT.LOG_N);
						//deserializeConfig(p.Me.CustomData);
						//p.Me.CustomData = lastCustomData = serializeConfig();
					}*/
				}

				if (blockCheckMachine != null)
				{
					if (!blockCheckMachine.MoveNext())
					{
						blockCheckMachine.Dispose();
						blockCheckMachine = null;
					}
				}
				else if (readConfig && tick % (5 * 60 * 60) == 0) mkBlockCheckMachine();
			}
			public string lastCustomData = "-1";

			IEnumerator<bool> blockCheckMachine = null;
			void mkBlockCheckMachine()
			{
				if (blockCheckMachine != null) blockCheckMachine.Dispose();
				blockCheckMachine = blockLoader();
				step = 0;
			}
			public int step = 0;

			public bool isThis(IMyTerminalBlock b)
			{
				return b.GetOwnerFactionTag() == p.Me.GetOwnerFactionTag() && b.CubeGrid == p.Me.CubeGrid;
			}
			public IEnumerator<bool> blockLoader()
			{
				var gts = p.GridTerminalSystem;
				consoleLog = null;
				statusLog = null;
				profileLog = null;
				List<IMyTerminalBlock> LCDs = new List<IMyTerminalBlock>();
				gts.GetBlocksOfType(LCDs, b => (b is IMyTextSurface) && b.CubeGrid == p.Me.CubeGrid);
				foreach (var b in LCDs)
				{
					IMyTextSurface s = b as IMyTextSurface;
					if (b.CustomData.Contains("statusLog")) statusLog = s;
					else if (b.CustomData.Contains("consoleLog")) consoleLog = s;
					else if (b.CustomData.Contains("profileLog")) profileLog = s;
					else if (b.CustomData.Contains("cargodbg")) cargodbg = s;
					else if (b.CustomName.Contains(AUTOCRAFT_LCD_KEYWORD)) autocraftingLCD = s;
				}
				step++;
				yield return true;
				LCDs.Clear();
				gts.GetBlocksOfType(LCDs, b => b.CubeGrid == p.Me.CubeGrid && b.CustomName.Contains("Conduit"));
				if(LCDs.Count > 0)conduit = LCDs[0];
				step++;
				yield return true;
				gts.GetBlocksOfType(controllers, isThis);
				step++;
				yield return true;
				step++;
				connectors.Clear();
				gts.GetBlocksOfType(connectors, isThis);
				yield return true;
				step++;

				if (USE_SKITS) gts.GetBlocksOfType(assemblers, isThis);
				else gts.GetBlocksOfType(assemblers, b => isThis(b) && b.DefinitionDisplayNameText != "Survival Kit");

				yield return true;
				step++;
				gts.GetBlocksOfType(refineries, b => isThis(b));

				yield return true;
				step++;
				gts.GetBlocksOfType(reactors, b => isThis(b));


				yield return true;
				step++;
				//gts.GetBlocksOfType(weaponCoreWeapons, b => b.CubeGrid == p.Me.CubeGrid && b.IsFunctional && APIWC.HasCoreWeapon(b));
				//yield return true;
				//step++;
				gts.GetBlocksOfType(inventoryBlocks, b => b.HasInventory && b.HasPlayerAccess(p.Me.OwnerId));
				yield return true;
				step++;
				if (neverFullyLoaded) log("BOOT DONE. " + ((DateTime.Now - bootTime).TotalSeconds).ToString("0.00") + "s wall (" + tick + "t)", LT.LOG_N);
				neverFullyLoaded = false;
				step++;
				yield return false;
			}
		}
	}
}
