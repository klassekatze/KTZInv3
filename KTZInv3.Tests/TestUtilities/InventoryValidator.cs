using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>One finding from the inventory validator.</summary>
    public sealed class InventoryViolation
    {
        public string Item;          // "MyObjectBuilder_Ore/Iron"
        public MyFixedPoint Amount;
        public string InContainer;   // where the item currently sits
        public string Reason;        // human-readable explanation
        public override string ToString() => $"  [{Item} x{Amount}] in '{InContainer}': {Reason}";
    }

    /// <summary>
    /// Validates the final state of a mock grid against the script's transfer
    /// rules. For every item in every container, checks that there is NO other
    /// container the item "should" be in:
    ///
    ///   1. higher priority (strictly — equal priority never moves),
    ///   2. accepting the item's category (or holdall / Alltypes),
    ///   3. with at least the category's minimum margin of free space
    ///      (non-negligible: 0.01 m^3 for fractional items, item volume
    ///      for integral ones — mirrors nonFractionalMinMarginByCat),
    ///   4. special-compliant: special containers only hold their declared
    ///      stocktargets; locked containers are skipped entirely.
    ///
    /// It also flags items sitting in a container that doesn't accept their
    /// category at all (the script would expel those).
    ///
    /// This is a TEST function: clarity over speed. It re-parses names the
    /// same way the script does (updateP) so the checks stand on their own
    /// and don't depend on internal script state.
    /// </summary>
    public static class InventoryValidator
    {
        static readonly Dictionary<string, string> Cattocargo = new Dictionary<string, string>
        {
            { "MyObjectBuilder_OxygenContainerObject", "Bottles" },
            { "MyObjectBuilder_GasContainerObject", "Bottles" },
            { "MyObjectBuilder_PhysicalGunObject", "Tools" },
            { "MyObjectBuilder_PhysicalObject", "Tools" },
            { "MyObjectBuilder_ConsumableItem", "Tools" },
            { "MyObjectBuilder_Datapad", "Tools" },
            { "MyObjectBuilder_AmmoMagazine", "Ammo" },
            { "MyObjectBuilder_Ore", "Ores" },
            { "MyObjectBuilder_Ingot", "Ingots" },
            { "MyObjectBuilder_Component", "Components" },
        };

        public static string CategoryOf(MyItemType type) =>
            Cattocargo.TryGetValue(type.TypeId, out var c) ? c : "Unknown";

        /// <summary>Parsed state of one container (mirrors updateP).</summary>
        public sealed class ContainerState
        {
            public string Name;
            public int Priority = 100000;         // default_p
            public bool Special;
            public bool Locked;
            public bool Hidden;
            public bool Holdall;
            public HashSet<string> Categories = new HashSet<string>();
            public List<MyItemType> StockTargets = new List<MyItemType>(); // special: declared targets
            public MyFixedPoint FreeVolume;
            public Dictionary<MyItemType, MyFixedPoint> Stuff = new Dictionary<MyItemType, MyFixedPoint>();
            public FakeInventory Inventory;
            public IMyTerminalBlock Block;
        }

        /// <summary>
        /// Validates a mock world. Returns a list of violations (empty = perfect).
        /// </summary>
        /// <param name="world">World built by BlueprintFactory or CargoFactory.</param>
        /// <param name="minMarginOverride">Optional per-category margin override; when
        /// null, margins are computed from item definitions like the script does.</param>
        public static List<InventoryViolation> Validate(BlueprintFactory.World world,
            Dictionary<string, MyFixedPoint> minMarginOverride = null)
        {
            var states = world.Cargos.Select(c => ParseContainer(c)).ToList();
            var violations = new List<InventoryViolation>();

            foreach (var c in states)
            {
                if (c.Locked || c.Hidden) continue; // script never moves anything here

                foreach (var (type, amount) in c.Stuff.ToList())
                {
                    var cat = CategoryOf(type);

                    // --- source-side rule: item in a container that doesn't accept its category ---
                    if (!c.Special && !c.Holdall && !c.Categories.Contains(cat))
                    {
                        violations.Add(new InventoryViolation
                        {
                            Item = type.ToString(),
                            Amount = amount,
                            InContainer = c.Name,
                            Reason = $"container does not accept category '{cat}' — the script would expel this item",
                        });
                        continue; // don't double-report; it shouldn't be here at all
                    }

                    // --- destination-side rule: is there a better home? ---
                    var margin = MarginFor(cat, type, minMarginOverride);
                    var better = FindBetterHome(c, type, cat, margin, states);
                    if (better != null)
                    {
                        violations.Add(new InventoryViolation
                        {
                            Item = type.ToString(),
                            Amount = amount,
                            InContainer = c.Name,
                            Reason = $"should be in '{better.Name}' (priority {better.Priority} < {c.Priority}, " +
                                     $"accepts '{cat}', free {better.FreeVolume:0.###} >= margin {margin:0.###})",
                        });
                    }
                }

                // --- special-compliance: special containers may only hold declared stocktargets ---
                if (c.Special && c.StockTargets.Count > 0)
                {
                    foreach (var (type, amount) in c.Stuff)
                    {
                        if (!c.StockTargets.Contains(type))
                        {
                            violations.Add(new InventoryViolation
                            {
                                Item = type.ToString(),
                                Amount = amount,
                                InContainer = c.Name,
                                Reason = $"special container does not declare '{type.SubtypeId}' in its stocktargets",
                            });
                        }
                    }
                }
            }

            return violations;
        }

        static ContainerState ParseContainer(CargoMock cargo)
        {
            var inv = cargo.AsFakeInventory();
            var c = new ContainerState
            {
                Name = cargo.Block.CustomName,
                Inventory = inv,
                Block = cargo.Block,
                FreeVolume = inv.MaxVolume - inv.CurrentVolume,
            };
            var items = new List<MyInventoryItem>();
            inv.GetItems(items);
            foreach (var it in items)
            {
                if (!c.Stuff.ContainsKey(it.Type)) c.Stuff[it.Type] = 0;
                c.Stuff[it.Type] += it.Amount;
            }

            var tokens = c.Name.Split(' ', '.');
            foreach (var tok in tokens)
            {
                var ltok = tok.ToLower();
                if (ltok.StartsWith("[") && ltok.EndsWith("]"))
                    ltok = ltok.Substring(1, ltok.Length - 2);

                if (ltok == "special") { c.Special = true; c.Priority -= 10000; }
                else if (ltok == "locked") c.Locked = true;
                else if (ltok == "hidden") { c.Locked = true; c.Hidden = true; }
                else if (ltok.StartsWith("p"))
                {
                    var ap = ltok.Substring(1);
                    if (ap == "max") c.Priority = int.MinValue;
                    else if (ap == "min") c.Priority = int.MaxValue;
                    else if (ap.All(char.IsDigit))
                    {
                        c.Priority -= 10000;
                        if (int.TryParse(ap, out var n) && n.ToString() == ap) c.Priority += n;
                    }
                }
                else if (ltok == "alltypes") c.Holdall = true;
                else
                {
                    foreach (var kvp in Cattocargo)
                        if (ltok == kvp.Value.ToLower()) { c.Categories.Add(kvp.Value); break; }
                }
            }
            if (c.Holdall)
                foreach (var v in Cattocargo.Values) c.Categories.Add(v);
            if (c.Special)
            {
                c.Categories.Clear();
                c.StockTargets.AddRange(ParseStockTargets(cargo.Block.CustomData));
            }
            return c;
        }

        /// <summary>Parses special-container CustomData ("TypeId/Subtype=count" lines, 'all' supported).</summary>
        static List<MyItemType> ParseStockTargets(string customData)
        {
            var list = new List<MyItemType>();
            if (string.IsNullOrEmpty(customData)) return list; // empty = alltypes
            foreach (var line in customData.Split('\n'))
            {
                var lr = line.Trim().Split('=');
                if (lr.Length != 2) continue;
                var ids = lr[0].Split('/');
                if (ids.Length != 2) continue;
                // the script parses stocktargets as getType("MyObjectBuilder_" + ids[0], ids[1])
                // (bpprefix is added, see Inventory_1.updateP) — mirror it exactly
                var typeId = ids[0].StartsWith("MyObjectBuilder_") ? ids[0] : "MyObjectBuilder_" + ids[0];
                try { list.Add(new MyItemType(typeId, ids[1])); } catch { }
            }
            return list;
        }

        static MyFixedPoint MarginFor(string cat, MyItemType type, Dictionary<string, MyFixedPoint> overrideMap)
        {
            if (overrideMap != null && overrideMap.TryGetValue(cat, out var m)) return m;
            // mirrors the script: 0.01 m^3 for fractional items, item volume for integral
            var nfo = type.GetItemInfo();
            return nfo.UsesFractions ? (MyFixedPoint)0.01 : (MyFixedPoint)nfo.Volume;
        }

        /// <summary>Finds a strictly-higher-priority container that should hold this item.</summary>
        static ContainerState FindBetterHome(ContainerState source, MyItemType type, string cat,
            MyFixedPoint margin, List<ContainerState> all)
        {
            ContainerState best = null;
            foreach (var d in all)
            {
                if (d == source) continue;
                if (d.Locked || d.Hidden) continue;          // script never targets these
                if (d.Special) continue;                     // special pulls its own stocktargets
                if (d.Priority >= source.Priority) continue; // strict: equal priority never moves
                if (!d.Categories.Contains(cat)) continue;   // must accept the category
                if (d.FreeVolume < margin) continue;         // non-negligible free space
                // pick the highest-priority (lowest number) candidate for the message
                if (best == null || d.Priority < best.Priority) best = d;
            }
            return best;
        }
    }
}
