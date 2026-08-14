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
		/// The learner is only used DURING isolated discovery (RefDiscover):
		/// continuous passive observation of normally-running refineries was
		/// removed because it is unnecessary (discovery is enactable) and the
		/// mixed windows are inherently less accurate. RefDiscover creates one
		/// fresh learner per discovery run, so the observation windows are
		/// always clean single-input ones.
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

			// forget the observation history so the next update takes a fresh
			// baseline (used when a discovery starts: the flush deltas must
			// not be attributed as consumption)
			public void reset()
			{
				lastInput = null;
				lastOutput = null;
			}
			// serializes the registry section (lines after the KTZREF; header).
			// One line per (refinery def, input), all outputs on the same
			// line as comma-separated pairs (the prefix is not repeated):
			//   refDef;input;out1;r1,out2;r2,out3;r3
			// MyDefinitionId.ToString() contains neither ',' nor ';', so the
			// delimiters are unambiguous. Old-format lines (one output per
			// line: refDef;input;output;ratio) still parse via
			// loadRegistryLine (they are a single comma-chunk).
			static public string writeRegistry()
			{
				StringBuilder sb = new StringBuilder();
				foreach (var defKvp in learned)
				{
					foreach (var oreKvp in defKvp.Value)
					{
						sb.Append('\n').Append(defKvp.Key.ToString())
						  .Append(';').Append(oreKvp.Key.ToString());
						bool first = true;
						foreach (var outKvp in oreKvp.Value)
						{
							sb.Append(first ? ';' : ',').Append(outKvp.Key.ToString())
							  .Append(';').Append(((double)outKvp.Value).ToString("0.###"));
							first = false;
						}
					}
				}
				return sb.ToString();
			}

			// parses one registry line (new or old format):
			//   new: refDef;input;out1;r1,out2;r2  (outputs comma-separated)
			//   old: refDef;input;output;ratio    (a single comma-chunk)
			static public void loadRegistryLine(string line)
			{
				var chunks = line.Split(',');
				if (chunks.Length < 1) return;
				try
				{
					var first = chunks[0].Split(';');
					if (first.Length < 4) return;
					var refDef = MyDefinitionId.Parse(first[0].Trim());
					var input = MyItemType.Parse(first[1].Trim());

					// one line = one (refDef, input): all comma-chunks are
					// output;ratio pairs of that single conversion
					var outs = new Dictionary<MyItemType, MyFixedPoint>();
					foreach (var chunk in chunks)
					{
						var parts = chunk.Split(';');
						// every chunk ends in ...;output;ratio: the first
						// chunk carries the refDef;input prefix (4+ parts),
						// continuation chunks are bare output;ratio pairs
						if (parts.Length < 2) continue;
						double ratio;
						if (!double.TryParse(parts[parts.Length - 1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ratio)) continue;
						outs[MyItemType.Parse(parts[parts.Length - 2].Trim())] = (MyFixedPoint)ratio;
					}
					if (outs.Count == 0) return;

					Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> learnedOre;
					if (!learned.TryGetValue(refDef, out learnedOre))
					{
						learnedOre = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();
						learned[refDef] = learnedOre;
					}
					// merge into the existing (refineryDef, input) entry:
					// old-format lines are one output per line, so each
					// subsequent line for the same input appends its output
					Dictionary<MyItemType, MyFixedPoint> existing;
					if (!learnedOre.TryGetValue(input, out existing))
					{
						existing = new Dictionary<MyItemType, MyFixedPoint>();
						learnedOre[input] = existing;
					}
					foreach (var kvp in outs) existing[kvp.Key] = kvp.Value;
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
