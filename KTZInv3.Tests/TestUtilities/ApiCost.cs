using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KTZInv3.Tests.TestUtilities
{
    /// <summary>
    /// Artificial per-call cost model for the game API calls the script makes.
    /// The unit-test fakes are free, so a profile of pure fake calls shows only
    /// OUR code — but in a real PB the dominant term is the game API itself
    /// (inventory reads, transfers, block queries, LCD writes). This model lets
    /// a profiling test pay the same wall-clock cost the live API would.
    ///
    /// Costs are measured on the live server via se-mcp (microseconds per call)
    /// and injected at the top of the fake API methods. DISABLED by default:
    /// regular tests stay fast; only profiling tests set Enabled=true.
    ///
    /// NOTE on precision: Thread.Sleep's granularity is ~1ms, but real API
    /// calls cost tens of microseconds. A calibrated busy-wait on Stopwatch
    /// achieves sub-ms precision and burns CPU only while Enabled.
    /// </summary>
    public enum ApiOp
    {
        InvGetItems,       // IMyInventory.GetItems(list) — cost scales with stack count
        InvGetItemAt,      // IMyInventory.GetItemAt(i)
        InvGetItemAmount,  // IMyInventory.GetItemAmount(type)
        InvTransfer,       // IMyInventory.TransferItemTo/From (a real move)
        InvAccepted,       // IMyInventory.GetAcceptedItems
        BlockGetInventory, // IMyTerminalBlock.GetInventory(i)
        GtsGetBlocks,      // GetBlocksOfType<T> (loader scans)
        AsmGetQueue,       // IMyAssembler.GetQueue
        CustomDataGet,     // block.CustomData getter (registry + name parsing)
        LcdWrite,          // IMyTextSurface.WriteText (status display)
    }

    public static class ApiCost
    {
        /// <summary>Master switch. Off = zero cost (normal tests).</summary>
        public static bool Enabled = false;

        /// <summary>Microseconds per call, per operation. Zero = free.</summary>
        public static readonly Dictionary<ApiOp, double> UsPerCall = new Dictionary<ApiOp, double>();

        /// <summary>Optional multiplier for the whole model (tune test duration).</summary>
        public static double Scale = 1.0;

        public static double Get(ApiOp op)
        {
            double us;
            return UsPerCall.TryGetValue(op, out us) ? us * Scale : 0;
        }

        /// <summary>
        /// Pay the modelled cost for the given API call. No-op unless Enabled.
        /// Busy-waits on a Stopwatch for sub-ms precision (Thread.Sleep's
        /// granularity is ~1ms, too coarse for tens-of-microseconds costs).
        /// </summary>
        public static void Apply(ApiOp op)
        {
            if (!Enabled) return;
            double us = Get(op);
            if (us <= 0) return;
            Spin(us);
        }

        /// <summary>Busy-wait for the given microseconds (calibrated spin).</summary>
        public static void Spin(double us)
        {
            if (us <= 0) return;
            long targetTicks = (long)(us * Stopwatch.Frequency / 1000000.0);
            long start = Stopwatch.GetTimestamp();
            long elapsed;
            do { elapsed = Stopwatch.GetTimestamp() - start; } while (elapsed < targetTicks);
        }

        /// <summary>Resets the model to zero cost and disabled.</summary>
        public static void Reset()
        {
            Enabled = false;
            Scale = 1.0;
            UsPerCall.Clear();
        }
    }
}
