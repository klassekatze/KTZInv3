using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using System;
using System.Collections.Generic;
using VRage;
using VRage.Game;
using VRageMath;

namespace IngameScript
{

	public class WcPbApi
	{
		public string[] WcBlockTypeLabels = new string[]
			{
				"Any",
				"Offense",
				"Utility",
				"Power",
				"Production",
				"Thrust",
				"Jumping",
				"Steering"
			};

		private Action<ICollection<MyDefinitionId>> a;
		private Func<IMyTerminalBlock, IDictionary<string, int>, bool> b;
		private Action<IMyTerminalBlock, IDictionary<MyDetectedEntityInfo, float>> c;
		private Func<long, bool> d;
		private Func<long, int, MyDetectedEntityInfo> e;
		private Func<IMyTerminalBlock, long, int, bool> f;
		private Action<IMyTerminalBlock, bool, bool, int> g;
		private Func<IMyTerminalBlock, bool> h;
		private Action<IMyTerminalBlock, ICollection<MyDetectedEntityInfo>> i;
		private Func<IMyTerminalBlock, ICollection<string>, int, bool> j;
		private Action<IMyTerminalBlock, ICollection<string>, int> k;
		private Func<IMyTerminalBlock, long, int, Vector3D?> l;

		private Func<IMyTerminalBlock, int, Matrix> m;
		private Func<IMyTerminalBlock, int, Matrix> n;
		private Func<IMyTerminalBlock, long, int, MyTuple<bool, Vector3D?>> o;
		private Func<IMyTerminalBlock, int, string> p;
		private Action<IMyTerminalBlock, int, string> q;
		private Func<long, float> r;
		private Func<IMyTerminalBlock, int, MyDetectedEntityInfo> s;
		private Action<IMyTerminalBlock, long, int> t;
		private Func<long, MyTuple<bool, int, int>> u;

		private Action<IMyTerminalBlock, bool, int> v;
		private Func<IMyTerminalBlock, int, bool, bool, bool> w;
		private Func<IMyTerminalBlock, int, float> x;
		private Func<IMyTerminalBlock, int, MyTuple<Vector3D, Vector3D>> y;
		private Func<IMyTerminalBlock, float> _getCurrentPower;
		public Func<Sandbox.ModAPI.Ingame.IMyTerminalBlock, float> _getHeatLevel;

		public bool isReady = false;
		IMyTerminalBlock pbBlock = null;
		public bool Activate(IMyTerminalBlock pbBlock)
		{
			this.pbBlock = pbBlock;
			var dict = pbBlock.GetProperty("WcPbAPI")?.As<IReadOnlyDictionary<string, Delegate>>().GetValue(pbBlock);
			if (dict == null) throw new Exception("WcPbAPI failed to activate");
			return ApiAssign(dict);
		}

		public bool ApiAssign(IReadOnlyDictionary<string, Delegate> delegates)
		{
			if (delegates == null)
				return false;
			AssignMethod(delegates, "GetCoreWeapons", ref a);
			AssignMethod(delegates, "GetBlockWeaponMap", ref b);
			AssignMethod(delegates, "GetSortedThreats", ref c);
			AssignMethod(delegates, "GetObstructions", ref i);
			AssignMethod(delegates, "HasGridAi", ref d);
			AssignMethod(delegates, "GetAiFocus", ref e);
			AssignMethod(delegates, "SetAiFocus", ref f);
			AssignMethod(delegates, "HasCoreWeapon", ref h);
			AssignMethod(delegates, "GetPredictedTargetPosition", ref l);
			AssignMethod(delegates, "GetTurretTargetTypes", ref j);
			AssignMethod(delegates, "SetTurretTargetTypes", ref k);
			AssignMethod(delegates, "GetWeaponAzimuthMatrix", ref m);
			AssignMethod(delegates, "GetWeaponElevationMatrix", ref n);
			AssignMethod(delegates, "IsTargetAlignedExtended", ref o);
			AssignMethod(delegates, "GetActiveAmmo", ref p);
			AssignMethod(delegates, "SetActiveAmmo", ref q);
			AssignMethod(delegates, "GetConstructEffectiveDps", ref r);
			AssignMethod(delegates, "GetWeaponTarget", ref s);
			AssignMethod(delegates, "SetWeaponTarget", ref t);
			AssignMethod(delegates, "GetProjectilesLockedOn", ref u);

			AssignMethod(delegates, "FireWeaponOnce", ref v);
			AssignMethod(delegates, "ToggleWeaponFire", ref g);
			AssignMethod(delegates, "IsWeaponReadyToFire", ref w);
			AssignMethod(delegates, "GetMaxWeaponRange", ref x);
			AssignMethod(delegates, "GetWeaponScope", ref y);

			AssignMethod(delegates, "GetCurrentPower", ref _getCurrentPower);
			AssignMethod(delegates, "GetHeatLevel", ref _getHeatLevel);

			//Delegate.CreateDelegate(null, null);

			isReady = true;
			return true;
		}
		private void AssignMethod<T>(IReadOnlyDictionary<string, Delegate> delegates, string name, ref T field) where T : class
		{
			if (delegates == null)
			{
				field = null;
				return;
			}
			Delegate del;
			if (!delegates.TryGetValue(name, out del))
				throw new Exception($"{GetType().Name} :: Couldn't find {name} delegate of type {typeof(T)}");
			field = del as T;
			if (field == null)
				throw new Exception(
					$"{GetType().Name} :: Delegate {name} is not type {typeof(T)}, instead it's: {del.GetType()}");
		}

		public void GetAllCoreWeapons(ICollection<MyDefinitionId> collection) => a?.Invoke(collection);
		public void GetSortedThreats(IDictionary<MyDetectedEntityInfo, float> collection) =>
			c?.Invoke(pbBlock, collection);
		public bool HasGridAi(long entity) => d?.Invoke(entity) ?? false;
		public MyDetectedEntityInfo? GetAiFocus(long shooter, int priority = 0) => e?.Invoke(shooter, priority);

		public bool SetAiFocus(IMyTerminalBlock pBlock, long target, int priority = 0) =>
			f?.Invoke(pBlock, target, priority) ?? false;

		public void ToggleWeaponFire(IMyTerminalBlock weapon, bool on, bool allWeapons, int weaponId = 0) =>
			g?.Invoke(weapon, on, allWeapons, weaponId);
		public bool HasCoreWeapon(IMyTerminalBlock weapon) => h?.Invoke(weapon) ?? false;

		public void GetObstructions(IMyTerminalBlock pBlock, ICollection<MyDetectedEntityInfo> collection) =>
			i?.Invoke(pBlock, collection);

		public Vector3D? GetPredictedTargetPosition(IMyTerminalBlock weapon, long targetEnt, int weaponId) =>
			l?.Invoke(weapon, targetEnt, weaponId) ?? null;

		public Matrix GetWeaponAzimuthMatrix(IMyTerminalBlock weapon, int weaponId) =>
			m?.Invoke(weapon, weaponId) ?? Matrix.Zero;

		public Matrix GetWeaponElevationMatrix(IMyTerminalBlock weapon, int weaponId) =>
			n?.Invoke(weapon, weaponId) ?? Matrix.Zero;

		public MyTuple<bool, Vector3D?> IsTargetAlignedExtended(IMyTerminalBlock weapon, long targetEnt, int weaponId) =>
			o?.Invoke(weapon, targetEnt, weaponId) ?? new MyTuple<bool, Vector3D?>();
		public string GetActiveAmmo(IMyTerminalBlock weapon, int weaponId) =>
			p?.Invoke(weapon, weaponId) ?? null;

		public void SetActiveAmmo(IMyTerminalBlock weapon, int weaponId, string ammoType) =>
			q?.Invoke(weapon, weaponId, ammoType);

		public float GetConstructEffectiveDps(long entity) => r?.Invoke(entity) ?? 0f;

		public MyDetectedEntityInfo? GetWeaponTarget(IMyTerminalBlock weapon, int weaponId = 0) =>
			s?.Invoke(weapon, weaponId);

		public void SetWeaponTarget(IMyTerminalBlock weapon, long target, int weaponId = 0) =>
			t?.Invoke(weapon, target, weaponId);

		public bool GetBlockWeaponMap(IMyTerminalBlock weaponBlock, IDictionary<string, int> collection) =>
			b?.Invoke(weaponBlock, collection) ?? false;

		public MyTuple<bool, int, int> GetProjectilesLockedOn(long victim) =>
			u?.Invoke(victim) ?? new MyTuple<bool, int, int>();

		public void FireWeaponOnce(IMyTerminalBlock weapon, bool allWeapons = true, int weaponId = 0) =>
				v?.Invoke(weapon, allWeapons, weaponId);


		public bool IsWeaponReadyToFire(IMyTerminalBlock weapon, int weaponId = 0, bool anyWeaponReady = true,
			bool shootReady = false) =>
			w?.Invoke(weapon, weaponId, anyWeaponReady, shootReady) ?? false;

		public float GetMaxWeaponRange(IMyTerminalBlock weapon, int weaponId) =>
			x?.Invoke(weapon, weaponId) ?? 0f;

		public MyTuple<Vector3D, Vector3D> GetWeaponScope(IMyTerminalBlock weapon, int weaponId) =>
			y?.Invoke(weapon, weaponId) ?? new MyTuple<Vector3D, Vector3D>();
		public float GetCurrentPower(IMyTerminalBlock weapon) => _getCurrentPower?.Invoke(weapon) ?? 0f;

		public float GetHeatLevel(Sandbox.ModAPI.Ingame.IMyTerminalBlock weapon) => _getHeatLevel?.Invoke(weapon) ?? 0f;
	}


}
