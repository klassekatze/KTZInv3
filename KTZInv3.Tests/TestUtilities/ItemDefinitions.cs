using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// Registers real <see cref="MyPhysicalItemDefinition"/>s into the game's
    /// definition manager so that <c>MyItemType.GetItemInfo()</c> — which KTZInv3
    /// calls heavily — returns real item data (Volume, MaxStackAmount, UsesFractions,
    /// IsOre/IsIngot) instead of NRE-ing on the null game singleton.
    ///
    /// The real classes are used wherever possible: <see cref="MyItemType"/>,
    /// <see cref="MyFixedPoint"/>, <see cref="MyInventoryItem"/> and the definition
    /// manager itself are all plain data / settable statics. Only the plumbing to
    /// construct the (private-ctor) manager and reach its internal definition
    /// dictionary needs reflection.
    /// </summary>
    public static class ItemDefinitions
    {
        static bool _registered = false;
        static readonly object _lock = new object();

        /// <summary>
        /// Call once per test process. Sets <c>MyDefinitionManagerBase.Static</c>
        /// to a real (empty) <c>MyDefinitionManager</c> and registers the item
        /// definitions used by the tests. Idempotent.
        /// </summary>
        public static void EnsureRegistered()
        {
            if (_registered) return;
            lock (_lock)
            {
                if (_registered) return;

                var managerType = typeof(MyDefinitionManager);
                var ctor = managerType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (ctor == null)
                    throw new InvalidOperationException("MyDefinitionManager private ctor not found");
                var manager = (MyDefinitionManager)ctor.Invoke(null);
                MyDefinitionManagerBase.Static = manager;

                // MyItemType("MyObjectBuilder_Component", ...) parses the type id
                // through the object-builder registry, which the game populates at
                // startup. Register the real OB types headless.
                MyObjectBuilderType.RegisterFromAssembly(typeof(MyObjectBuilder_Component).Assembly, registerLegacyNames: true);
                // block OBs (refineries etc.) live in SpaceEngineers.ObjectBuilders.dll;
                // needed by the refinery recipe registry round-trip tests
                MyObjectBuilderType.RegisterFromAssembly(typeof(MyObjectBuilder_Refinery).Assembly, registerLegacyNames: true);

                var dict = GetDefinitionsById(manager);
                foreach (var def in BuildDefinitions())
                    dict[def.Id] = def;

                _registered = true;
            }
        }

        /// <summary>
        /// Reaches <c>MyDefinitionManager.m_definitions.m_definitionsById</c>
        /// (internal field on the internal DefinitionSet) via reflection.
        /// </summary>
        static IDictionary<MyDefinitionId, MyDefinitionBase> GetDefinitionsById(MyDefinitionManager manager)
        {
            // MyDefinitionManagerBase.m_definitions (protected) -> DefinitionSet
            var baseField = typeof(MyDefinitionManagerBase).GetField("m_definitions", BindingFlags.Instance | BindingFlags.NonPublic);
            var definitionSet = baseField?.GetValue(manager);
            if (definitionSet == null)
                throw new InvalidOperationException("m_definitions is null");

            // The dictionary lives on the DefinitionSet (internal class); if the
            // ctor hasn't initialized it yet, create one and install it.
            var setType = definitionSet.GetType();
            var byIdField = setType.GetField("m_definitionsById", BindingFlags.Instance | BindingFlags.NonPublic);
            var byId = byIdField?.GetValue(definitionSet);
            if (byId == null)
            {
                var dictType = setType.GetNestedType("DefinitionDictionary`1", BindingFlags.NonPublic)
                    ?? AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => SafeTypes(a))
                        .FirstOrDefault(t => t.Name == "DefinitionDictionary`1" && typeof(IDictionary<MyDefinitionId, MyDefinitionBase>).IsAssignableFrom(t));
                if (dictType == null)
                    throw new InvalidOperationException("DefinitionDictionary type not found");
                byId = Activator.CreateInstance(dictType, 100);
                byIdField.SetValue(definitionSet, byId);
            }
            return (IDictionary<MyDefinitionId, MyDefinitionBase>)byId;
        }

        static IEnumerable<Type> SafeTypes(System.Reflection.Assembly a)
        {
            try { return a.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        /// <summary>
        /// Registers a single physical item definition (used by the blueprint
        /// factory for item subtypes not in the built-in set). Idempotent.
        /// </summary>
        public static void RegisterItem(string typeId, string subtypeId, float volume, float mass, MyFixedPoint maxStack)
        {
            EnsureRegistered();
            var def = new MyPhysicalItemDefinition
            {
                Id = new MyDefinitionId(MyObjectBuilderType.Parse(typeId), subtypeId),
                Volume = volume,
                Mass = mass,
                MaxStackAmount = maxStack,
                Enabled = true,
                Public = true,
            };
            lock (_lock)
            {
                var dict = GetDefinitionsById((MyDefinitionManager)MyDefinitionManagerBase.Static);
                dict[def.Id] = def;
            }
        }

        static List<MyDefinitionBase> BuildDefinitions()
        {
            var list = new List<MyDefinitionBase>();

            // Components (integral amounts, volume in m^3, max stack 1000)
            list.Add(Component("SteelPlate", 0.0001f, 0.5f, 1000));
            list.Add(Component("ConstructionComponent", 0.0001f, 0.5f, 1000));
            list.Add(Component("InteriorPlate", 0.00005f, 0.2f, 1000));
            list.Add(Component("Motor", 0.0001f, 0.5f, 1000));
            list.Add(Component("LargeTube", 0.0001f, 0.5f, 1000));

            // Ores (fractional kg, 0.37 L/kg = 0.00037 m^3/kg)
            list.Add(Ore("Stone", 0.00037f, 1.0f));
            list.Add(Ore("Iron", 0.00037f, 1.0f));

            // Ingots (fractional kg, 0.27 L/kg = 0.00027 m^3/kg)
            list.Add(Ingot("Iron", 0.00027f, 1.0f));

            return list;
        }

        static MyPhysicalItemDefinition Component(string subtype, float volume, float mass, MyFixedPoint maxStack)
        {
            return new MyPhysicalItemDefinition
            {
                Id = new MyDefinitionId(typeof(MyObjectBuilder_Component), subtype),
                Volume = volume,
                Mass = mass,
                MaxStackAmount = maxStack,
                Enabled = true,
                Public = true,
            };
        }

        static MyPhysicalItemDefinition Ore(string subtype, float volume, float mass)
        {
            return new MyPhysicalItemDefinition
            {
                Id = new MyDefinitionId(typeof(MyObjectBuilder_Ore), subtype),
                Volume = volume,
                Mass = mass,
                MaxStackAmount = (MyFixedPoint)1000000,
                Enabled = true,
                Public = true,
            };
        }

        static MyPhysicalItemDefinition Ingot(string subtype, float volume, float mass)
        {
            return new MyPhysicalItemDefinition
            {
                Id = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), subtype),
                Volume = volume,
                Mass = mass,
                MaxStackAmount = (MyFixedPoint)1000000,
                Enabled = true,
                Public = true,
            };
        }
    }
}
