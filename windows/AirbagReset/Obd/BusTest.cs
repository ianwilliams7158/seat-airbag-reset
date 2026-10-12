using System;
using System.Collections.Generic;
using System.Linq;

namespace AirbagReset.Obd
{
    /// <summary>
    /// Diagnostic checks for when the airbag module does not answer: is the adapter powered, does anything
    /// on the CAN bus answer, and do airbag replies arrive on unexpected IDs (or get lost by a buggy ATCRA filter)?
    /// </summary>
    public static class BusTest
    {
        public static string Run(Elm327 elm, Action<string> log)
        {
            var summary = new List<string>();

            log("=== Adapter set-up ===");
            elm.InitialiseForRawCan();
            var voltage = Try(elm, "ATRV");
            summary.Add("Voltage at adapter: " + (voltage.FirstOrDefault() ?? "unknown"));
            Try(elm, "ATDP");

            log("=== Test 1: engine ECU, standard OBD (proves the adapter can talk on CAN) ===");
            Try(elm, "ATCAF1");
            Try(elm, "ATH1");
            Try(elm, "ATCRA");      // no receive filter: show every reply
            Try(elm, "ATSH7DF");
            var engine = Try(elm, "0100", 5000);
            bool engineOk = engine.Any(l => l.Replace(" ", "").StartsWith("7E"));
            summary.Add("Engine (standard OBD): " + (engineOk ? "ANSWERED - adapter and CAN bus OK" : "no answer"));

            log("=== Test 2: airbag UDS (715), no receive filter ===");
            Try(elm, "ATSH715");
            Try(elm, "ATFCSH715");
            Try(elm, "ATFCSD300000");
            Try(elm, "ATFCSM1");
            var uds = Try(elm, "3E00", 3000).Concat(Try(elm, "22F187", 3000)).ToList();
            summary.Add("Airbag UDS (715): " + Describe(uds));

            log("=== Test 3: airbag TP2.0 (200 -> 0x15), no receive filter ===");
            Try(elm, "ATCAF0");
            Try(elm, "ATSH200");
            var tpAirbag = Try(elm, "15C00010000301", 3000);
            summary.Add("Airbag TP2.0: " + Describe(tpAirbag));

            log("=== Test 4: engine TP2.0 (200 -> 0x01), proves TP2.0 works on this car ===");
            var tpEngine = Try(elm, "01C00010000301", 3000);
            summary.Add("Engine TP2.0: " + Describe(tpEngine));
            var engineChannel = tpEngine.Select(l => l.Replace(" ", "")).FirstOrDefault(l => l.Length >= 17 && l.Substring(5, 2) == "D0");
            if (engineChannel != null)
            {
                // Politely close the channel the engine just opened.
                int tx = Convert.ToInt32(engineChannel.Substring(11, 2), 16) | ((Convert.ToInt32(engineChannel.Substring(13, 2), 16) & 0x07) << 8);
                Try(elm, "ATSH" + tx.ToString("X3"));
                Try(elm, "A8");
            }

            Try(elm, "ATCAF1");
            return "Bus test results:\r\n\r\n" + string.Join("\r\n", summary) +
                   "\r\n\r\nPlease copy the whole communication log (click in it, Ctrl+A, Ctrl+C) and send it to me.";
        }

        private static string Describe(List<string> lines)
        {
            var frames = lines.Where(l => l != "NO DATA" && l != "OK" && l != "?").ToList();
            return frames.Count == 0 ? "no answer" : "ANSWERED: " + string.Join(" | ", frames.Take(4));
        }

        private static List<string> Try(Elm327 elm, string cmd, int timeoutMs = 3000)
        {
            try
            {
                return elm.Command(cmd, timeoutMs);
            }
            catch (ElmException)
            {
                return new List<string>();
            }
        }
    }
}
