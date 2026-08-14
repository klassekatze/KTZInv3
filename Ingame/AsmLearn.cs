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

			// serializes the registry section (lines after the KTZREC; header).
			// One line per item, all ingredients on the same line as
			// comma-separated pairs (the item prefix is not repeated):
			//   item;ingredient1;amount1,ingredient2;amount2
			// MyItemType.ToString() contains neither ',' nor ';', so the
			// delimiters are unambiguous. Old-format lines (one ingredient
			// per line: item;ingredient;amount) still parse.
			static public string writeRegistry()
			{
				StringBuilder sb = new StringBuilder();
				foreach (var itemKvp in known)
				{
					sb.Append('\n').Append(itemKvp.Key.ToString());
					bool first = true;
					foreach (var ingKvp in itemKvp.Value)
					{
						sb.Append(first ? ';' : ',').Append(ingKvp.Key.ToString())
						  .Append(';').Append(((double)ingKvp.Value).ToString("0.###"));
						first = false;
					}
				}
				return sb.ToString();
			}

			// parses one registry line (new or old format):
			//   new: item;ing1;a1,ing2;a2   (ingredients comma-separated)
			//   old: item;ingredient;amount (a single comma-chunk)
			static public void loadRegistryLine(string line)
			{
				var chunks = line.Split(',');
				if (chunks.Length < 1) return;
				try
				{
					var first = chunks[0].Split(';');
					if (first.Length < 3) return;
					var item = MyItemType.Parse(first[0].Trim());

					// one line = one item: all comma-chunks are
					// ingredient;amount pairs of that item's composition
					var comp = new Dictionary<MyItemType, MyFixedPoint>();
					foreach (var chunk in chunks)
					{
						var parts = chunk.Split(';');
						if (parts.Length < 2) continue;
						double amount;
						if (!double.TryParse(parts[parts.Length - 1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out amount)) continue;
						comp[MyItemType.Parse(parts[parts.Length - 2].Trim())] = (MyFixedPoint)amount;
					}
					if (comp.Count == 0) return;
					// merge into the existing item entry: old-format lines
					// are one ingredient per line, so each subsequent line
					// for the same item appends its ingredient
					Dictionary<MyItemType, MyFixedPoint> existing;
					if (!known.TryGetValue(item, out existing))
					{
						existing = new Dictionary<MyItemType, MyFixedPoint>();
						known[item] = existing;
					}
					foreach (var kvp in comp) existing[kvp.Key] = kvp.Value;
				}
				catch (Exception) { }
			}
		}
	}
}
