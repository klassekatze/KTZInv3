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
	partial class Program : MyGridProgram
	{


		static string[] common_ammo_identifiers = new string[]
						{
						"missile",
						"ammo",
						"magazine",
						"torpedo",
						"slug",
						"box"
						};
		static Dictionary<string, string> bulkreplace = new Dictionary<string, string>() {
			{"AngleGrinder","Grinder"},
			{"CrateTomato","Crate of Tomatoes"},
			{"HeavyArms","Heavy Armaments"},
			{"GravityGenerator","Gravity Comp."},
			{"RadioCommunication","Radio-comm Comp."},
			{"Detector","Detector Comp."},
			{"LargeTube","Large Steel Tube"},
			{"Construction","Construction Comp."},
			{"UltimateAutomatic",""},
			{"AryxLynxon",""},
			{"TungstenUranium","TU"},
			{"LeadSteel","LS"},
			{"EStabilizer","Stabilizer"}
		};
		static Dictionary<MyItemType, string> prettyItemNames = new Dictionary<MyItemType, string>();
		static public string prettyItemName(MyItemType item)
		{
			string r = "";
			if (prettyItemNames.TryGetValue(item, out r)) return r;
			else
			{
				return prettyItemNames[item] = _prettyItemName(item);
			}
		}
		static string _prettyItemName(MyItemType item)
		{
			//initbulk();
			string name = item.SubtypeId.Replace("MyObjectBuilder_", "").Replace("_", " ");
			var nfo = Inventory.getItemInfo(item);
			foreach (KeyValuePair<string, string> kvp in bulkreplace)
			{
				if (name == kvp.Key)
				{
					name = kvp.Value;
					break;
				}
				else if (name.StartsWith(kvp.Key))
				{
					name = name.Replace(kvp.Key, kvp.Value);
					break;
				}
			}
			if (nfo.IsAmmo)
			{
				var l = name.ToLower();
				if (name.Length > 20) name = name.Replace("Magazine", "");
				else
				{

					if (name.StartsWith("Missile"))
					{
						name = name.Substring("Missile".Length) + " Missile";
					}
				}
				l = name.ToLower();
				bool id = false;
				foreach (var i in common_ammo_identifiers)
				{
					if (l.IndexOf(i) != -1)
					{
						id = true;
						break;
					}
				}
				if (!id)
				{
					name += "Ammo";
				}
			}
			if (nfo.IsTool && name.Length > 5)
			{

				string nsub = name.Substring(0, name.Length - 5);
				if (name.EndsWith("1Item")) name = nsub;
				else if (name.EndsWith("2Item")) name = nsub + " (Enhanced)";
				else if (name.EndsWith("3Item")) name = nsub + " (Proficient)";
				else if (name.EndsWith("4Item")) name = nsub + " (Elite)";
			}
			int capcount = 0;
			foreach (char c in name) if (char.IsUpper(c)) capcount += 1;
			if (capcount <= 1)
			{
				if (nfo.IsOre && name != "Stone") return name + " Ore";
				if (nfo.IsIngot)
				{
					if (name == "Stone") return "Gravel";
					return name + " Ingot";
				}
			}
			else
			{
				string rename = "";
				for (int i = 0; i < name.Length; i++)
				{
					if (i > 0 && i < name.Length - 1)
					{
						bool notlast = true;
						if (rename.Length > 0) notlast = rename[rename.Length - 1] != ' ';
						if (name[i - 1] != ' ' && name[i] != ' ' && name[i + 1] != ' ' && notlast)
						{
							bool prev = char.IsUpper(name[i - 1]);
							bool cur = char.IsUpper(name[i]);
							bool next = char.IsUpper(name[i + 1]);
							bool nextLetter = char.IsLetter(name[i + 1]);
							if ((cur && !next && nextLetter && name[i - 1] != '(' && name[i] != ' ') || (!prev && name[i - 1] != '(' && cur && name[i] != ' '))// && !prev)
							{
								rename += " ";
							}
							if (prev && !char.IsLetter(name[i]) && name[i] != ' ') rename += " ";
						}
					}
					rename += name[i]; ;
				}
				name = rename;
			}
			name = name.Replace(" Adv ", " Advanced ");
			name = name.Replace(" Component", " Comp.");
			if (nfo.IsAmmo && name.Length > 4)
			{
				if (name.EndsWith("MCRN")) name = name.Substring(0, name.Length - 4) + "(MCRN)";
				if (name.EndsWith("UNN")) name = name.Substring(0, name.Length - 3) + "(UNN)";

			}
			if (name.EndsWith(" Item")) name = name.Substring(0, name.Length - 5);
			if (name.Length > 25) name = name.Replace(" ", "");
			if (name.Length > 25)
			{
				name = name.Replace("(", "").Replace(")", "");
			}
			return name;
		}

		int nDigits(int i)
		{
			if (i < 0) i = -i;
			if (i < 10) return 1;
			if (i < 100) return 2;
			if (i < 1000) return 3;
			if (i < 10000) return 4;
			if (i < 100000) return 5;
			if (i < 1000000) return 6;
			if (i < 10000000) return 7;
			if (i < 100000000) return 8;
			if (i < 1000000000) return 9;
			return 10;
		}

		public enum disp
		{
			LEFTLEFT,
			RIGHTRIGHT,
			LEFTRIGHT
		}

		public string listInv(Dictionary<MyItemType, MyFixedPoint> manifest, Func<MyItemType, bool> filter = null, disp display = disp.LEFTLEFT)
		{
			string r = "";
			int nlen = 0;
			int vlen = 0;
			Dictionary<string, string> entries = new Dictionary<string, string>();
			foreach (KeyValuePair<MyItemType, MyFixedPoint> kvp in manifest)
			{
				if (filter != null) if (!filter(kvp.Key)) continue;

				string val = "";// NaN";
				if (kvp.Value < 1000) val = kvp.Value.ToString();
				else if (kvp.Value < 1000000) val = (((double)kvp.Value) / 1000).ToString("0.0") + "k";
				else /*if (kvp.Value < 1000000000) */val = (((double)kvp.Value) / 1000000).ToString("0.0") + "M";
				string key = prettyItemName(kvp.Key);
				entries[key] = val;
				if (key.Length > nlen) nlen = key.Length;
				if (val.Length > vlen) vlen = val.Length;
			}
			foreach (KeyValuePair<string, string> kvp in entries)
			{
				if (display == disp.LEFTLEFT)
				{
					string d = kvp.Key;
					if (d.Length < nlen) d = new string(' ', nlen - d.Length) + d;
					r += d + ": " + kvp.Value + "\n";
				}
				else if (display == disp.RIGHTRIGHT)
				{
					string v = kvp.Value;
					if (v.Length < vlen) v = new string(' ', vlen - v.Length) + v;
					r += v + ": " + kvp.Key + "\n";
				}
				else
				{
					string d = kvp.Key;
					if (d.Length < nlen) d += new string('_', nlen - d.Length);// + d;
					r += d + ": " + kvp.Value + "\n";
				}
			}
			return r;
		}
	}
}
