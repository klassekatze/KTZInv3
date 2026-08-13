using Sandbox.Game.GameSystems;
using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage.Game.ModAPI.Ingame;
using VRage.Utils;

namespace IngameScript
{
	partial class Program : MyGridProgram
	{
		//int condCurUpdate = -1;

		//const string TAG = "conduit.example.v1";
		// Build "[CDT:<tag>]\n<json>". The JSON is a small example, the grid's name/id and its
		// inventory totaled by item subtype. Replace the payload with whatever you want to export.

		int lpkttick = -10000;
		string lpkt = "";
		public void conduitUpdate()
		{
			if (!MAKE_CONDUIT_PACKET || !gInv.hasUpdatedOnce) return;
			if((_ticks - lpkttick) > 60*3 && conduit != null)
			{
				lpkttick = _ticks;
				var pkt = BuildPacket();
				if (pkt != lpkt)
				{
					conduit.CustomData = pkt;
				}
			}
		}
		private long _pid = DateTime.UtcNow.Ticks;   // unique per script run
		private long _seq = 0;
		string BuildPacket()
		{
			_seq++;

			var grid = Me.CubeGrid;

			var totals = new Dictionary<string, double>();

			var TAG = "conduit."+grid.CustomName+".v1";

			var sb = new StringBuilder();
			sb.Append("[CDT:").Append(TAG).Append("]\n");
			sb.Append("{\"grid\":").Append(Json(grid.CustomName));
			sb.Append(",\"entityId\":").Append(grid.EntityId);
			sb.Append(",\"pid\":").Append(_pid);
			sb.Append(",\"seq\":").Append(_seq);
			sb.Append(",\"inventory\":[");
			bool first = true;
			foreach (var kv in Inventory.globalManifest.stuff)
			{
				if (!first) sb.Append(',');
				first = false;

				var nfo = Inventory.getItemInfo(kv.Key);
				var type = "";

				type = nfo.IsOre ? "Ore" : nfo.IsIngot ? "Ingot" : nfo.IsAmmo ? "Ammo" : nfo.IsComponent ? "Component" : nfo.IsTool ? "Tool" : "Unknown";

				sb.Append("{\"subtype\":")
				.Append(Json(kv.Key.SubtypeId))
				.Append(",\"type\":")
				.Append(Json(type))
				.Append(",\"amount\":")
				.Append(Num((double)kv.Value)).Append('}');
			}
			sb.Append("]}");
			return sb.ToString();
		}

		// Minimal JSON string escaping (quotes, backslashes, control chars).
		static string Json(string s)
		{
			if (s == null) return "\"\"";
			var sb = new StringBuilder("\"");
			foreach (char c in s)
			{
				if (c == '"' || c == '\\') sb.Append('\\');
				sb.Append(c < ' ' ? ' ' : c);
			}
			return sb.Append('"').ToString();
		}

		static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
	}
}
