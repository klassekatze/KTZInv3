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
		/// Observational refinery learner: watches a refinery-like machine's
		/// input/output inventories and learns, from the deltas between
		/// successive observations, what each input item converts into and at
		/// what ratio. Unlike BPLearn2 (which reads the production queue), this
		/// works purely from inventory deltas, so it also covers modded
		/// refinery-like machines that don't expose a meaningful queue.
		///
		/// Knowledge is keyed by the refinery's BLOCK DEFINITION, so a recipe
		/// learned on a regular refinery does NOT apply to a blast forge or
		/// other advanced refinery type (e.g. SDX2 gives boron only from the
		/// blast forge for stone). RefDiscover uses this to know which
		/// (refinery type, ore) pairs are still unknown and worth discovering.
		///
		/// Attribution rule: an observation window is only learned from when
		/// EXACTLY ONE input item type was consumed. With several ores being
		/// refined at once the output deltas can't be attributed to a single
		/// input, so such windows are skipped. Every output item type that
		/// increased in that window is recorded against the single consumed
		/// input, each with its own ratio (stone -> gravel + iron + nickel +
		/// silicon etc). Ratios are rolling totals (total produced / total
		/// consumed) so partial windows and sorter pulls average out over time.
		/// </summary>
		class RefLearn
		{
			public IMyProductionBlock machine = null;

			// learned conversions, shared across all learners on the grid:
			// refinery block definition -> input item type -> { output item type -> ratio (produced per consumed) }
			static public Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>> learned = new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>();

			// rolling accumulators backing the ratios above
			static Dictionary<MyDefinitionId, Dictionary<MyItemType, MyFixedPoint>> consumedTotal = new Dictionary<MyDefinitionId, Dictionary<MyItemType, MyFixedPoint>>();
			static Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>> producedTotal = new Dictionary<MyDefinitionId, Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>>();

			// last observed inventory snapshots (compacted per type)
			List<MyInventoryItem> lastInput = null;
			List<MyInventoryItem> lastOutput = null;

			// every live learner, so RefDiscover can reset the baseline of the
			// learner bound to a refinery it is about to flush
			static List<RefLearn> allLearners = new List<RefLearn>();

			public RefLearn()
			{
				allLearners.Add(this);
			}

			// whether we know the recipe for the given refinery block definition and ore
			static public bool knowsRecipe(MyDefinitionId refDef, MyItemType ore)
			{
				Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> byOre;
				if (!learned.TryGetValue(refDef, out byOre)) return false;
				return byOre.ContainsKey(ore);
			}

			// outputs (with ratios) for a known recipe; empty when unknown
			static public Dictionary<MyItemType, MyFixedPoint> outputsFor(MyDefinitionId refDef, MyItemType ore)
			{
				Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> byOre;
				if (!learned.TryGetValue(refDef, out byOre)) return new Dictionary<MyItemType, MyFixedPoint>();
				Dictionary<MyItemType, MyFixedPoint> outs;
				if (!byOre.TryGetValue(ore, out outs)) return new Dictionary<MyItemType, MyFixedPoint>();
				return outs;
			}

			// forget the observation history of every learner bound to the
			// given machine so the next update takes a fresh baseline (used
			// when RefDiscover flushes and stuffs the refinery: the flush
			// deltas must not be attributed as consumption)
			static public void resetForMachine(IMyProductionBlock m)
			{
				foreach (var l in allLearners)
				{
					if (l.machine == m)
					{
						l.lastInput = null;
						l.lastOutput = null;
					}
				}
			}

			// serializes the registry section (lines after the KTZREF; header)
			static public string writeRegistry()
			{
				StringBuilder sb = new StringBuilder();
				foreach (var defKvp in learned)
				{
					foreach (var oreKvp in defKvp.Value)
					{
						foreach (var outKvp in oreKvp.Value)
						{
							sb.Append('\n').Append(defKvp.Key.ToString())
							  .Append(';').Append(oreKvp.Key.ToString())
							  .Append(';').Append(outKvp.Key.ToString())
							  .Append(';').Append(((double)outKvp.Value).ToString("0.###"));
						}
					}
				}
				return sb.ToString();
			}

			// parses one registry line: refineryDef;input;output;ratio
			static public void loadRegistryLine(string line)
			{
				var s2 = line.Split(';');
				if (s2.Length < 4) return;
				try
				{
					var refDef = MyDefinitionId.Parse(s2[0].Trim());
					var input = MyItemType.Parse(s2[1].Trim());
					var output = MyItemType.Parse(s2[2].Trim());
					double ratio;
					if (!double.TryParse(s2[3].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ratio)) return;
					Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> byOre;
					if (!learned.TryGetValue(refDef, out byOre))
					{
						byOre = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();
						learned[refDef] = byOre;
					}
					Dictionary<MyItemType, MyFixedPoint> outs;
					if (!byOre.TryGetValue(input, out outs))
					{
						outs = new Dictionary<MyItemType, MyFixedPoint>();
						byOre[input] = outs;
					}
					outs[output] = (MyFixedPoint)ratio;
				}
				catch (Exception) { }
			}

			public void update()
			{
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.RefLearn) : false; }
				// refineries only tick once per second, so faster observation is meaningless
				if (REFINERY_LEARN && tick % 60 == 0 && machine != null)
				{
					var refDef = (MyDefinitionId)machine.BlockDefinition;

					var input = compact(machine.InputInventory);
					var output = compact(machine.OutputInventory);

					if (lastInput != null)
					{
						// consumed: types present in the last input snapshot that
						// decreased or vanished; produced: types in the output
						// snapshot that increased or appeared.
						Dictionary<MyItemType, MyFixedPoint> consumed = diffConsumed(lastInput, input);
						Dictionary<MyItemType, MyFixedPoint> produced = diffProduced(lastOutput, output);

						if (consumed.Count == 1 && produced.Count > 0)
						{
							MyItemType inputType = consumed.Keys.First();
							MyFixedPoint inAmt = consumed[inputType];

							Dictionary<MyItemType, MyFixedPoint> ct;
							if (!consumedTotal.TryGetValue(refDef, out ct))
							{
								ct = new Dictionary<MyItemType, MyFixedPoint>();
								consumedTotal[refDef] = ct;
							}
							MyFixedPoint c;
							ct.TryGetValue(inputType, out c);
							ct[inputType] = c + inAmt;

							Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> pt;
							if (!producedTotal.TryGetValue(refDef, out pt))
							{
								pt = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();
								producedTotal[refDef] = pt;
							}
							Dictionary<MyItemType, MyFixedPoint> pt2;
							if (!pt.TryGetValue(inputType, out pt2))
							{
								pt2 = new Dictionary<MyItemType, MyFixedPoint>();
								pt[inputType] = pt2;
							}
							foreach (var kvp in produced)
							{
								MyFixedPoint cur;
								pt2.TryGetValue(kvp.Key, out cur);
								pt2[kvp.Key] = cur + kvp.Value;
							}

							// refresh ratios from the rolling totals
							Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> byOre;
							if (!learned.TryGetValue(refDef, out byOre))
							{
								byOre = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();
								learned[refDef] = byOre;
							}
							Dictionary<MyItemType, MyFixedPoint> ratios;
							if (!byOre.TryGetValue(inputType, out ratios))
							{
								ratios = new Dictionary<MyItemType, MyFixedPoint>();
								byOre[inputType] = ratios;
							}
							foreach (var kvp in pt2)
							{
								ratios[kvp.Key] = (MyFixedPoint)((double)kvp.Value / (double)ct[inputType]);
							}

							log("RefLearn: " + refDef.SubtypeId + ": " + inputType.SubtypeId + " -> " + string.Join(", ", pt2.Select(kvp => kvp.Key.SubtypeId + " x" + ((double)kvp.Value / (double)ct[inputType]).ToString("0.###"))), LT.LOG_N);
						}
					}

					lastInput = input;
					lastOutput = output;
				}
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.RefLearn) : false; }
			}

			// snapshots an inventory compacted per item type (sums amounts,
			// first-seen ItemId), mirroring BPLearn2's compaction
			static List<MyInventoryItem> compact(IMyInventory inv)
			{
				List<MyInventoryItem> items = new List<MyInventoryItem>();
				inv.GetItems(items);
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
					if (c > 0) itemsCompact.Add(new MyInventoryItem(t, firstIds[t], c));
				}
				return itemsCompact;
			}

			// item types whose amount decreased or vanished between snapshots
			static Dictionary<MyItemType, MyFixedPoint> diffConsumed(List<MyInventoryItem> before, List<MyInventoryItem> after)
			{
				var res = new Dictionary<MyItemType, MyFixedPoint>();
				foreach (var b in before)
				{
					MyFixedPoint a = 0;
					foreach (var i in after)
					{
						if (i.Type == b.Type) { a = i.Amount; break; }
					}
					if (a < b.Amount) res[b.Type] = b.Amount - a;
				}
				return res;
			}

			// item types whose amount increased or appeared between snapshots
			static Dictionary<MyItemType, MyFixedPoint> diffProduced(List<MyInventoryItem> before, List<MyInventoryItem> after)
			{
				var res = new Dictionary<MyItemType, MyFixedPoint>();
				foreach (var a in after)
				{
					MyFixedPoint b = 0;
					foreach (var i in before)
					{
						if (i.Type == a.Type) { b = i.Amount; break; }
					}
					if (a.Amount > b) res[a.Type] = a.Amount - b;
				}
				return res;
			}
		}
	}
}
