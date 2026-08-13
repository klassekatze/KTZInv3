using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IngameScript
{
	public partial class Program : MyGridProgram
	{
		// bapp is kept: it is used by Logging.cs and Inventory_1.cs (genstatus),
		// NOT part of the Profiler system.
		static void bapp(StringBuilder b, params object[] args)
		{
			foreach (object a in args)
			{
				b.Append(a.ToString());
			}
		}

		// ============================================================================
		// Profiler system DISABLED (redundant - superseded by the Diag seam,
		// see Diag.cs). All instantiations/calls/display code were commented out
		// across the script; these class definitions are kept only for reference.
		// ============================================================================
		/*
		public class Stopwatch
		{
			DateTime start;
			public Stopwatch()
			{
			start = DateTime.Now;
			}
			public double e()
			{
				return (DateTime.Now - start).TotalMilliseconds;
			}
		}


		public class Profiler
		{


			static bool PROFILING_ENABLED = true;
			static List<Profiler> profilers = new List<Profiler>();
			const int mstracklen = 60;
			double[] mstrack = new double[mstracklen];
			double msdiv = 1.0d / mstracklen;
			int mscursor = 0;
			DateTime start_time = DateTime.MinValue;
			string Name = "";
			string pre = "";
			string post = "";
			int _ticks_between_calls = 1;
			int ltick = int.MinValue;
			//..int callspertick = 1;

			static int base_sort_position_c = 0;
			int base_sort_position = 0;

			bool nevercalled = true;
			//bool closed = true;
			public int getSortPosition()
			{
				if (nevercalled) return int.MaxValue;
				int mult = (int)Math.Pow(10, 8 - (depth * 2));
				if (parent != null) return parent.getSortPosition() + (base_sort_position * mult);
				return base_sort_position * mult;
			}
			static int basep = (int)Math.Pow(10, 5);
			public Profiler(string name)
			{
				if (PROFILING_ENABLED)
				{
					Name = name;
					profilers.Add(this);
					for(var i =0; i < mstracklen; i++)mstrack[i] = 0;
					base_sort_position = base_sort_position_c;
					base_sort_position_c += 1;
				}
			}
			public void s()
			{
				start();
			}
			public void e()
			{
				stop();
			}
			static List<Profiler> stack = new List<Profiler>();
			Profiler parent = null;
			int depth = 0;
			bool adding = false;
			public void start()
			{
				if (PROFILING_ENABLED)
				{
					//closed = false;
					nevercalled = false;
					if (tick != ltick)
					{
						if (_ticks_between_calls == 1 && ltick != int.MinValue)
						{
							_ticks_between_calls = tick - ltick;
						}else
						{
							var tbc = tick - ltick;
							if (tbc != _ticks_between_calls)
							{
								_ticks_between_calls = 1;
								for (var i = 0; i < mstracklen; i++) mstrack[i] = 0;
							}
						}

						ltick = tick;
						//callspertick = 1;
						adding = false;
					}
					else
					{
						adding = true;
					}
					if (depth == 0) depth = stack.Count;
					if (depth > 11) depth = 11;
					if (stack.Count > 0 && parent == null) parent = stack[stack.Count - 1];
					stack.Add(this);
					start_time = DateTime.Now;
				}
			}
			double lastms = 0;
			double average = 0;


			/// <summary>
			/// records a fake ms consumption for this timeframe - for tests or demo
			/// </summary>
			public double FAKE_stop(double fakems)
			{
				return stop(fakems);
			}
			/// <summary>
			/// adds the elapsed time since start() to the records
			/// </summary>
			public double stop()
			{
				double time = 0;
				if (PROFILING_ENABLED)
				{
					//closed = true;
					time = (DateTime.Now - start_time).TotalMilliseconds;
				}
				return stop(time);
			}

			private double stop(double _ms)
			{
				double time = 0;
				if (PROFILING_ENABLED)
				{
					time = _ms;

					stack.Pop();
					if (parent != null)
					{
						depth = parent.depth + 1;
					}

					//if(!adding)mscursor = (mscursor + 1) % mstracklen;


					if (!adding) mstrack[mscursor] = 0;
					mstrack[mscursor] += time;
					if (!adding) mscursor = (mscursor + 1) % mstracklen;

					average = 0d;
					foreach (double ms in mstrack) average += ms;
					average *= msdiv;
					average /= _ticks_between_calls;
					lastms = time;
				}
				return time;
			}
			/// <summary>
			/// generates a monospaced report text. If called every tick, every 120 ticks it will recalculate treeview data.
			/// </summary>
			//the treeview can be initially inaccurate as some profilers might not be called every tick, depending on program architecture
			public string getReport(StringBuilder bu)
			{
				if (PROFILING_ENABLED)
				{
					if (tick % 120 == 25)//recalculate hacky treeview data, delayed by 25 ticks from program start
					{
						try
						{
							profilers.Sort(delegate (Profiler x, Profiler y)
							{
								return x.getSortPosition().CompareTo(y.getSortPosition());
							});
						}
						catch (Exception) { }

						for (int i = 0; i < profilers.Count; i++)
						{
							Profiler p = profilers[i];

							p.pre = "";
							if (p.depth > 0 && p.parent != null)
							{
								bool parent_has_future_siblings = false;
								bool has_future_siblings_under_parent = false;
								for (int b = i + 1; b < profilers.Count; b++)
								{
									if (profilers[b].depth == p.parent.depth) parent_has_future_siblings = true;
									if (profilers[b].depth == p.depth) has_future_siblings_under_parent = true;
									if (profilers[b].depth < p.depth) break;

								}
								while (p.pre.Length < p.parent.depth)
								{
									if (parent_has_future_siblings) p.pre += "│";
									else p.pre += " ";
								}
								bool last = false;

								if (!has_future_siblings_under_parent)
								{
									if (i < profilers.Count - 1)
									{
										if (profilers[i + 1].depth != p.depth) last = true;
									}
									else last = true;
								}
								if (last) p.pre += "└";
								else p.pre += "├";
								while (p.pre.Length < p.depth) p.pre += "─";
							}
						}
						int mlen = 0;
						foreach (Profiler p in profilers) if (p.pre.Length + p.Name.Length > mlen) mlen = p.pre.Length + p.Name.Length;
						foreach (Profiler p in profilers)
						{
							p.post = "";
							int l = p.pre.Length + p.Name.Length + p.post.Length;
							if (l < mlen) p.post = new string('_', mlen - l);
						}
					}
					if (nevercalled) bapp(bu, "!!!!", Name , "!!!!: NEVER CALLED!");
					else bapp(bu, pre, Name, post, ": " , lastms.ToString("0.00") , ";", average.ToString("0.00"));
				}
				return "";
			}
			static public string getAllReports()
			{
				StringBuilder b = new StringBuilder();
				//string r = "";
				if (PROFILING_ENABLED)
				{
					foreach (Profiler watch in profilers)
					{
						watch.getReport(b);
						b.Append("\n");
					}
				}
				if (stack.Count > 0)
				{
					bapp(b,"profile stack error:\n", stack.Count, "\n");
					foreach (var s in stack)
					{
						bapp(b, s.Name, ",");
					}
				}
				return b.ToString();
			}
		}
		*/
	}
}
