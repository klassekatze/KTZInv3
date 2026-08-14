using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;
using VRage.GameServices;
using VRageRender.Voxels;

namespace IngameScript
{
	public partial class Program : MyGridProgram
	{
		class Autocraft
		{
			//Dictionary<MyItemType, MyDefinitionId> bpcache;


			static public MyItemType nop = MyItemType.MakeComponent("SteelPlate");
			static Dictionary<string, MyItemType> typecast = new Dictionary<string, MyItemType>();	
			public static bool canFind(string subtype, out MyItemType t)
			{
				if(typecast.ContainsKey(subtype))
				{
					t = typecast[subtype];
					return true;
				}
				foreach (var bpkvp in blueprints)
				{
					if (bpkvp.Key.SubtypeId.ToString() == subtype)
					{
						typecast[subtype] = bpkvp.Key;
						t = bpkvp.Key;
						return true;
					}
				}
				t = nop;
				return false;
			}



			static public Dictionary<string, int> quotas = new Dictionary<string, int>();
			static public Dictionary<MyDefinitionId, int> quotas_bp = new Dictionary<MyDefinitionId, int>();

			static public Dictionary<MyDefinitionId, MyDefinitionId> blueprints = new Dictionary<MyDefinitionId, MyDefinitionId>();
			static public void addBP(MyDefinitionId item, MyDefinitionId bp)
			{
				blueprints[item] = bp;
				writeCD();
			}
			static public void writeCD()
			{
				string newcd = "KTZINV;\nitemID;blueprintID";
				foreach (var kvp in blueprints)
				{
					newcd += "\n" + kvp.Key.ToString() + ";" + kvp.Value.ToString();
				}
				// refinery recipe registry (read back by the ctor alongside the
				// assembler bps)
				newcd += "\nKTZREF;";
				newcd += RefLearn.writeRegistry();
				// assembler recipe compositions (read back by the ctor too)
				newcd += "\nKTZREC;";
				newcd += AsmLearn.writeRegistry();
				gProgram.Me.CustomData = newcd;
			}


			public Autocraft()
			{
				var cd = gProgram.Me.CustomData;
				var spl = cd.Split('\n');
				string section = "KTZINV";
				foreach(var l in spl)
				{
					if (l.StartsWith("KTZREF;"))
					{
						section = "KTZREF";
						continue;
					}
					if (l.StartsWith("KTZREC;"))
					{
						section = "KTZREC";
						continue;
					}
					if (section == "KTZREF")
					{
						RefLearn.loadRegistryLine(l);
						continue;
					}
					if (section == "KTZREC")
					{
						AsmLearn.loadRegistryLine(l);
						continue;
					}
					var s2 = l.Split(';');
					if(s2.Length >= 2)
					{
						try
						{
							var itembp = MyDefinitionId.Parse(s2[0]);
							try
							{
								var recipebp = MyDefinitionId.Parse(s2[1]);
								blueprints[itembp] = recipebp;
							}
							catch (Exception) { }
						}
						catch (Exception){}
					}
				}
			}

			//key=item, val=production bp
			//static Profiler p1 = new Profiler("p1");
			//static Profiler p2 = new Profiler("p2");
			//static Profiler p3 = new Profiler("p3");
			public string writeLCD()
			{
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.AutoAvail) : false; }
				//p1.s();
				Dictionary<string, int> avail = new Dictionary<string, int>();
				
				foreach(var kvp in Inventory.globalManifest.stuff)
				{
					string subtype = kvp.Key.SubtypeId;//.Substring("MyObjectBuilder_".Length);//kvp.Key.SubtypeId.Replace("MyObjectBuilder_", "").Replace("_", " ");
					if (!quotas.ContainsKey(subtype))
					{
						var nfo = Inventory.getItemInfo(kvp.Key);
						if (!nfo.IsOre && !nfo.IsIngot)quotas[subtype] = 0;
						else
						{
							MyItemType derp = Autocraft.nop;
							if(canFind(subtype, out derp))
							{
								quotas[subtype] = 0;
								break;
							}

							/*foreach (var bpkvp in blueprints)
							{
								if (bpkvp.Key.SubtypeId.ToString() == kvp.Key.SubtypeId)
								{
									quotas[name] = 0;
									break;
								}
							}*/
						}
						//todo check if we have a bp, if bp ignore type status, fusion fuel etc
					}
					avail[subtype] = (int)kvp.Value;
				}
				//p1.e();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.AutoAvail) : false; }
				{ var _ = DEBUGGING ? diag.Enter(DbgLabel.AutoReport) : false; }
				//p2.s();
				StringBuilder b = new StringBuilder("Component Current | Wanted\n");

				foreach (var kvp in quotas)
				{
					int av = 0;
					int quota = kvp.Value;
					avail.TryGetValue(kvp.Key, out av);

					b.Append(kvp.Key);
					b.Append(" ");
					b.Append(av);
					b.Append(" ");

					if (av < quota) b.Append("<");
					else if (av == quota) b.Append("=");
					else b.Append(">");

					b.Append(" ");
					b.Append(quota);

					bool hasbp = false;
					{ var _ = DEBUGGING ? diag.Enter(DbgLabel.AutoBpCheck) : false; }
					//p3.s();
					foreach (var bpkvp in blueprints)
					{
						if (bpkvp.Key.SubtypeId.ToString() == kvp.Key)
						{
							hasbp = true;
							quotas_bp[bpkvp.Key] = quota;
							break;
						}
					}
					//p3.e();
					{ var _ = DEBUGGING ? diag.Exit(DbgLabel.AutoBpCheck) : false; }
					if (!hasbp) b.Append(" (no BP)");
					b.Append("\n");
				}


				/*foreach (var kvp in quotas)
				{
					int av = 0;
					int quota = kvp.Value;
					avail.TryGetValue(kvp.Key, out av);
					r += kvp.Key + " " + av+" ";
					if (av < quota) r += "<";
					else if (av == quota) r += "=";
					else r += ">";
					r += " " + quota;
					bool hasbp = false;
					p3.s();
					foreach (var bpkvp in blueprints)
					{
						if(bpkvp.Key.SubtypeId.ToString() == kvp.Key)
						{
							hasbp = true;
							break;
						}
					}
					p3.e();
					if (!hasbp) r += " (no BP)";
					r += "\n";
				}*/
				//p2.e();
				{ var _ = DEBUGGING ? diag.Exit(DbgLabel.AutoReport) : false; }
				return b.ToString();
			}
			string last = "";
			bool firstread = true;
			public void readLCD(string s)
			{
				if (last == s) return;

				last = s;
				var lines = s.Split('\n');
				foreach(var l in lines)
				{
					if (l.Contains("|")) continue;

					var tok = l.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
					if (tok.Length >= 4)
					{
						string key = tok[0];
						string cur = tok[1];
						char sym = tok[2][0];
						if (sym == '<' || sym == '>' || sym == '=')
						{
							string want = tok[3];
							int wnt = 0;
							var isn = int.TryParse(want, out wnt);
							if (isn)
							{
								quotas[key] = wnt;
							}
						}

					}
				}
			}
			
			
		}
	}
}
