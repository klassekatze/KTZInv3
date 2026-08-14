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
		/// <summary>
		/// Assembler recipe composition registry. While RefLearn infers
		/// refinery conversions from inventory deltas (ratios, since a
		/// refinery has no queue to read), an assembler recipe is EXACT: we
		/// know the blueprint, so disassembling one copy of the item yields
		/// the precise ingredient amounts that make it up. AsmDiscover drives
		/// that process and stores the result here, keyed by the produced
		/// item type: item -> { ingredient -> amount per unit of item }.
		///
		/// Persisted to CustomData in the KTZREC; section alongside the
		/// assembler blueprint lines and the refinery recipe registry.
		/// </summary>
		class AsmLearn
		{
			// item type -> { ingredient type -> amount per 1 unit of item }
			static public Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> known = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();

			// whether we know the exact composition of the given item
			static public bool knowsRecipe(MyItemType item)
			{
				return known.ContainsKey(item);
			}

			// the exact composition (ingredient -> amount per unit); empty
			// when unknown
			static public Dictionary<MyItemType, MyFixedPoint> compositionFor(MyItemType item)
			{
				Dictionary<MyItemType, MyFixedPoint> comp;
				if (known.TryGetValue(item, out comp)) return comp;
				return new Dictionary<MyItemType, MyFixedPoint>();
			}

			// records the exact composition learned from a disassembly run
			static public void record(MyItemType item, Dictionary<MyItemType, MyFixedPoint> composition)
			{
				known[item] = composition;
			}

			// serializes the registry section (lines after the KTZREC; header):
			// item;ingredient;amount (amount per 1 unit of item)
			static public string writeRegistry()
			{
				StringBuilder sb = new StringBuilder();
				foreach (var itemKvp in known)
				{
					foreach (var ingKvp in itemKvp.Value)
					{
						sb.Append('\n').Append(itemKvp.Key.ToString())
						  .Append(';').Append(ingKvp.Key.ToString())
						  .Append(';').Append(((double)ingKvp.Value).ToString("0.###"));
					}
				}
				return sb.ToString();
			}

			// parses one registry line: item;ingredient;amount
			static public void loadRegistryLine(string line)
			{
				var s2 = line.Split(';');
				if (s2.Length < 3) return;
				try
				{
					var item = MyItemType.Parse(s2[0].Trim());
					var ingredient = MyItemType.Parse(s2[1].Trim());
					double amount;
					if (!double.TryParse(s2[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out amount)) return;
					Dictionary<MyItemType, MyFixedPoint> comp;
					if (!known.TryGetValue(item, out comp))
					{
						comp = new Dictionary<MyItemType, MyFixedPoint>();
						known[item] = comp;
					}
					comp[ingredient] = (MyFixedPoint)amount;
				}
				catch (Exception) { }
			}
		}
	}
}
