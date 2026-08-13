using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FakeItEasy;
using Sandbox.ModAPI.Ingame;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// Port of the MDK2 UnitTestExample Gateway: builds an
    /// <see cref="IngameScript.Program"/> instance without running the game,
    /// wiring the MyGridProgram backend (Runtime, Echo, Me, Storage,
    /// GridTerminalSystem, IGC) to fakes.
    ///
    /// Echo is mocked by default: captured messages land in
    /// <see cref="EchoMessages"/> so tests can assert on them.
    /// </summary>
    public static class Gateway
    {
        /// <summary>Messages passed to Program.Echo during the test.</summary>
        public static readonly List<string> EchoMessages = new List<string>();

        public static ProgramBuilder CreateProgram()
        {
            EchoMessages.Clear();
            var echo = new Action<string>(EchoMessages.Add);
            return new ProgramBuilder(null, null, null, null, echo, string.Empty);
        }

        public readonly struct ProgramBuilder
        {
            private readonly IMyIntergridCommunicationSystem _igc;
            private readonly IMyGridTerminalSystem _gridTerminalSystem;
            private readonly IMyGridProgramRuntimeInfo _runtime;
            private readonly IMyProgrammableBlock _me;
            private readonly Action<string> _echo;
            private readonly string _storage;

            public ProgramBuilder(IMyIntergridCommunicationSystem igc, IMyGridTerminalSystem gridTerminalSystem, IMyGridProgramRuntimeInfo runtime, IMyProgrammableBlock me, Action<string> echo, string storage)
            {
                _igc = igc;
                _gridTerminalSystem = gridTerminalSystem;
                _runtime = runtime;
                _me = me;
                _echo = echo;
                _storage = storage;
            }

            public ProgramBuilder WithIgc(IMyIntergridCommunicationSystem igc) =>
                new ProgramBuilder(igc, _gridTerminalSystem, _runtime, _me, _echo, _storage);

            public ProgramBuilder WithGridTerminalSystem(IMyGridTerminalSystem gridTerminalSystem) =>
                new ProgramBuilder(_igc, gridTerminalSystem, _runtime, _me, _echo, _storage);

            public ProgramBuilder WithRuntime(IMyGridProgramRuntimeInfo runtime) =>
                new ProgramBuilder(_igc, _gridTerminalSystem, runtime, _me, _echo, _storage);

            public ProgramBuilder WithMe(IMyProgrammableBlock me) =>
                new ProgramBuilder(_igc, _gridTerminalSystem, _runtime, me, _echo, _storage);

            public ProgramBuilder WithEcho(Action<string> echo) =>
                new ProgramBuilder(_igc, _gridTerminalSystem, _runtime, _me, echo, _storage);

            public ProgramBuilder WithStorage(string storage) =>
                new ProgramBuilder(_igc, _gridTerminalSystem, _runtime, _me, _echo, storage);

            private Func<IMyIntergridCommunicationSystem> GetIgcContextGetter()
            {
                var igc = _igc ?? A.Fake<IMyIntergridCommunicationSystem>();
                return () => igc;
            }

            private IMyGridTerminalSystem GetGridTerminalSystem() =>
                _gridTerminalSystem ?? A.Fake<IMyGridTerminalSystem>();

            private string GetStorage() => _storage ?? string.Empty;

            private IMyProgrammableBlock GetMe() =>
                _me ?? A.Fake<IMyProgrammableBlock>();

            private Action<string> GetEcho() => _echo ?? Console.WriteLine;

            private IMyGridProgramRuntimeInfo GetRuntime() =>
                _runtime ?? A.Fake<IMyGridProgramRuntimeInfo>();

            public IngameScript.Program Build()
            {
                var program = FormatterServices.GetUninitializedObject(typeof(IngameScript.Program));

                if (!(program is Sandbox.ModAPI.IMyGridProgram backend))
                    throw new InvalidOperationException("No IMyGridProgram interface found.");

                backend.Runtime = GetRuntime();
                backend.Echo = GetEcho();
                backend.Me = GetMe();
                backend.Storage = GetStorage();
                backend.GridTerminalSystem = GetGridTerminalSystem();
                backend.IGC_ContextGetter = GetIgcContextGetter();

                var ctor = typeof(IngameScript.Program).GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                    throw new InvalidOperationException("No parameterless constructor found.");

                ctor.Invoke(program, null);
                return (IngameScript.Program)program;
            }
        }
    }
}
