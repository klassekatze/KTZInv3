using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.ModAPI.Ingame;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// Builds the "WcPbAPI" terminal property payload that the game's WeaponCore
    /// mod would inject into the programmable block. KTZInv3's ResourceLoader
    /// refuses to advance past its first step unless
    /// <c>WcPbApi.Activate(Me)</c> succeeds, and Activate requires a delegate
    /// dictionary containing every single name with the exact delegate type
    /// declared in WcPbApi's private fields — ApiAssign throws otherwise.
    ///
    /// Dummy delegates are created with Expression.Lambda so their runtime type
    /// matches the private field type exactly. All return default values
    /// (HasCoreWeapon -> false, etc.), which is what the sorting logic needs.
    /// </summary>
    public static class WcPbApiMocker
    {
        // public API name -> private delegate field name in WcPbApi.cs
        static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            ["GetCoreWeapons"] = "a",
            ["GetBlockWeaponMap"] = "b",
            ["GetSortedThreats"] = "c",
            ["GetObstructions"] = "i",
            ["HasGridAi"] = "d",
            ["GetAiFocus"] = "e",
            ["SetAiFocus"] = "f",
            ["HasCoreWeapon"] = "h",
            ["GetPredictedTargetPosition"] = "l",
            ["GetTurretTargetTypes"] = "j",
            ["SetTurretTargetTypes"] = "k",
            ["GetWeaponAzimuthMatrix"] = "m",
            ["GetWeaponElevationMatrix"] = "n",
            ["IsTargetAlignedExtended"] = "o",
            ["GetActiveAmmo"] = "p",
            ["SetActiveAmmo"] = "q",
            ["GetConstructEffectiveDps"] = "r",
            ["GetWeaponTarget"] = "s",
            ["SetWeaponTarget"] = "t",
            ["GetProjectilesLockedOn"] = "u",
            ["FireWeaponOnce"] = "v",
            ["ToggleWeaponFire"] = "g",
            ["IsWeaponReadyToFire"] = "w",
            ["GetMaxWeaponRange"] = "x",
            ["GetWeaponScope"] = "y",
            ["GetCurrentPower"] = "_getCurrentPower",
            ["GetHeatLevel"] = "_getHeatLevel",
        };

        static Dictionary<string, Delegate> _cache;

        /// <summary>
        /// A dictionary of dummy delegates, one per WcPbApi field, keyed by the
        /// public API name. Created once per process; the delegate instances are
        /// stateless so sharing them across tests is safe.
        /// </summary>
        public static IReadOnlyDictionary<string, Delegate> Delegates
        {
            get
            {
                if (_cache != null) return _cache;
                var type = typeof(IngameScript.WcPbApi);
                var dict = new Dictionary<string, Delegate>(Map.Count);
                foreach (var kv in Map)
                {
                    var fld = type.GetField(kv.Value, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("WcPbApi field '" + kv.Value + "' not found");
                    dict[kv.Key] = DummyOf(fld.FieldType);
                }
                _cache = dict;
                return _cache;
            }
        }

        /// <summary>Creates a delegate of the exact given type that returns default(TResult).</summary>
        static Delegate DummyOf(Type delegateType)
        {
            var invoke = delegateType.GetMethod("Invoke");
            var parameters = invoke.GetParameters()
                .Select(p => Expression.Parameter(p.ParameterType, p.Name))
                .ToArray();
            Expression body = invoke.ReturnType == typeof(void)
                ? Expression.Empty()
                : Expression.Default(invoke.ReturnType);
            return Expression.Lambda(delegateType, body, parameters).Compile();
        }
    }
}
