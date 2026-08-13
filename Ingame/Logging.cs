using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IngameScript
{
	partial class Program : MyGridProgram
	{
		public enum LT
		{
			LOG_N = 0,
			LOG_D,
			LOG_DD
		}
		string[] logtype_labels = { "INFO","_DBG","DDBG"};

		public static LT LOG_LEVEL = LT.LOG_N;
		public static Logger logger = new Logger();
		public static void log(string s, LT level)
		{
			Logger.log(s, level);
		}
		public static void log(string s)
		{
			Logger.log(s, LT.LOG_N);
		}

		public class Logger
		{
			public class logmsg
			{
				public logmsg(string m,string m2, LT l) { msg = m; msg_raw = m2; level = l; }
				public string msg = "";
				public string msg_raw = "";
				public int c = 1;
				public LT level = LT.LOG_N;
			}

			static List<logmsg> loggedMessages = new List<logmsg>();
			static int MAX_LOG = 50;
			static List<logmsg> superLoggedMessages = new List<logmsg>();
			static int MAX_SUPER_LOG = 1000;

			static public bool loggedMessagesDirty = true;

			public static void log(string s, LT level)
			{
				if (level > LOG_LEVEL) return;
				// LCD gate: no console LCD -> no one can read these messages, so
				// skip the accumulation entirely (no allocations, no list churn).
				// The display path (hypermain) already checks consoleLog; this
				// moves the gate to the write side so log() itself is free
				// without a log LCD. Superlog ("log" command -> controller
				// CustomData) shares the same gate: without an LCD you are not
				// running a logging setup.
				if (consoleLog == null) return;
				string s2 = s;
				if (s.Length > 50)
				{
					List<string> tok = new List<string>();
					while (s.Length > 50)
					{
						int c = 0;
						if (tok.Count > 0) c = 2;
						tok.Add(s.Substring(0, 50 - c));
						s = s.Substring(50 - c);
					}
					tok.Add(s);
					s = string.Join("\n ", tok);
				}
				var p = gProgram;
				logmsg l = null;
				if (loggedMessages.Count > 0)
				{
					l = loggedMessages[loggedMessages.Count - 1];
				}
				if (l != null)
				{
					if (l.msg == s) l.c += 1;
					else loggedMessages.Add(new logmsg(s, s2, level));
				}
				else loggedMessages.Add(new logmsg(s, s2, level));
				if (loggedMessages.Count > MAX_LOG) loggedMessages.RemoveAt(0);

				l = null;
				if (superLoggedMessages.Count > 0)
				{
					l = superLoggedMessages[superLoggedMessages.Count - 1];
				}
				if (l != null)
				{
					if (l.msg == s) l.c += 1;
					else superLoggedMessages.Add(new logmsg(s, s2, level));
				}
				else superLoggedMessages.Add(new logmsg(s, s2, level));
				if (superLoggedMessages.Count > MAX_SUPER_LOG) superLoggedMessages.RemoveAt(0);

				loggedMessagesDirty = true;
			}


			static public string loggedMessagesRender = "";
			static public void updateLoggedMessagesRender()
			{
				if (!loggedMessagesDirty) return;
				StringBuilder b = new StringBuilder();
				//if (!loggedMessagesDirty) return;// loggedMessagesRender;

				
				foreach (var m in loggedMessages)
				{
					b.Append(m.msg);
					if (m.c > 1) bapp(b," (" ,m.c ,")");
					b.Append("\n");
				}
				string o = b.ToString();
				loggedMessagesDirty = false;
				loggedMessagesRender = o;
			}
			static public void writeSuperlog()
			{
				StringBuilder b = new StringBuilder();
				//if (!loggedMessagesDirty) return;// loggedMessagesRender;


				foreach (var m in superLoggedMessages)
				{
					b.Append(m.msg);
					if (m.c > 1) bapp(b, " (", m.c, ")");
					b.Append("\n");
				}
				string o = b.ToString();
				controllers[0].CustomData = o;
				log(controllers[0].CustomName, LT.LOG_N);
			}
		}
	}
}
