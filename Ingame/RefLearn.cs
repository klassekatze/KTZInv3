using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;
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
			// input item type -> { output item type -> ratio (produced per consumed) }
			static public Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> learned = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();

			// rolling accumulators backing the ratios above
			static Dictionary<MyItemType, MyFixedPoint> consumedTotal = new Dictionary<MyItemType, MyFixedPoint>();
			static Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>> producedTotal = new Dictionary<MyItemType, Dictionary<MyItemType, MyFixedPoint>>();

			// last observed inventory snapshots (compacted per type)
			List<MyInventoryItem> lastInput = null;
			List<MyInventoryItem> lastOutput = null;

			public void update()
			{
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.RefLearn) : false; }
				// refineries only tick once per second, so faster observation is meaningless
				if (REFINERY_LEARN && tick % 60 == 0 && machine != null)
				{
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

							MyFixedPoint ct;
							consumedTotal.TryGetValue(inputType, out ct);
							consumedTotal[inputType] = ct + inAmt;

							Dictionary<MyItemType, MyFixedPoint> pt;
							if (!producedTotal.TryGetValue(inputType, out pt))
							{
								pt = new Dictionary<MyItemType, MyFixedPoint>();
								producedTotal[inputType] = pt;
							}
							foreach (var kvp in produced)
							{
								MyFixedPoint cur;
								pt.TryGetValue(kvp.Key, out cur);
								pt[kvp.Key] = cur + kvp.Value;
							}

							// refresh ratios from the rolling totals
							Dictionary<MyItemType, MyFixedPoint> ratios;
							if (!learned.TryGetValue(inputType, out ratios))
							{
								ratios = new Dictionary<MyItemType, MyFixedPoint>();
								learned[inputType] = ratios;
							}
							foreach (var kvp in pt)
							{
								ratios[kvp.Key] = (MyFixedPoint)((double)kvp.Value / (double)consumedTotal[inputType]);
							}

							log("RefLearn: " + inputType.SubtypeId + " -> " + string.Join(", ", pt.Select(kvp => kvp.Key.SubtypeId + " x" + ((double)kvp.Value / (double)consumedTotal[inputType]).ToString("0.###"))), LT.LOG_N);
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
