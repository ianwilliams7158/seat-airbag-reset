using System;
using System.Collections.Generic;
using System.Linq;

namespace AirbagReset.Obd
{
    /// <summary>
    /// Read-only diagnostic checks for when the airbag module does not answer. Only sends harmless
    /// requests (OBD mode 01/09, UDS "tester present" and read-identification, TP2.0 channel set-up).
    /// </summary>
    public static class BusTest
    {
        private static readonly (int Address, string Name)[] Tp20Modules =
        {
            (0x01, "Engine"), (0x02, "Gearbox"), (0x03, "ABS"), (0x08, "Climate"), (0x09, "Central electrics"),
            (0x15, "Airbag"), (0x16, "Steering wheel"), (0x17, "Instruments"), (0x19, "Gateway"),
            (0x44, "Power steering"), (0x46, "Comfort"), (0x56, "Radio"),
        };

        public static string Run(Elm327 elm, Action<string> log)
        {
            var summary = new List<string>();

            log("=== Adapter set-up ===");
            elm.InitialiseForRawCan();
            summary.Add("Voltage at adapter: " + (Try(elm, "ATRV").FirstOrDefault() ?? "unknown"));

            log("=== Test 1: engine ECU, standard OBD ===");
            Try(elm, "ATCAF1");
            Try(elm, "ATH1");
            Try(elm, "ATSH7DF");
            bool engineOk = Answered(Try(elm, "0100", 5000), "7E");
            summary.Add("1. Engine standard OBD: " + (engineOk ? "OK" : "no answer"));

            log("=== Test 2: VIN ===");
            var vin = Try(elm, "0902", 5000);
            summary.Add("2. VIN reply: " + (vin.Count == 0 ? "none" : string.Join(" | ", vin)));

            log("=== Test 3: engine UDS read part number (7E0) ===");
            Try(elm, "ATSH7E0");
            Try(elm, "ATFCSH7E0");
            Try(elm, "ATFCSD300000");
            Try(elm, "ATFCSM1");
            summary.Add("3. Engine UDS (7E0): " + Describe(Try(elm, "22F187", 3000)));

            log("=== Test 4: raw mode check - hand-built OBD request with auto-formatting OFF ===");
            Try(elm, "ATCAF0");
            Try(elm, "ATSH7DF");
            bool rawOk = Answered(Try(elm, "020100", 3000), "7E");
            summary.Add("4. Raw mode (ATCAF0) sending: " + (rawOk ? "OK - adapter sends raw frames correctly" : "FAILED - engine ignored the raw frame, this adapter mangles raw frames"));

            log("=== Test 5: TP2.0 roll call (exact frame length, airbag first) ===");
            var found = new List<int>();
            var tpAnswers = new List<string>();
            Try(elm, "ATV1");
            foreach (var (address, name) in Tp20Modules.OrderBy(m => m.Address == 0x15 ? 0 : 1))
            {
                var reply = OpenAndClose(elm, address);
                if (reply != null)
                {
                    found.Add(address);
                    tpAnswers.Add(name + " (" + address.ToString("X2") + "): " + reply);
                }
            }
            summary.Add("5. TP2.0 modules answering: " + (tpAnswers.Count == 0 ? "none" : "\r\n     " + string.Join("\r\n     ", tpAnswers)));

            if (!found.Contains(0x15))
            {
                log("=== Test 6: airbag not at 0x15 - scanning TP2.0 addresses 00-7F ===");
                var extra = new List<string>();
                for (int address = 0x00; address <= 0x7F; address++)
                {
                    if (found.Contains(address)) continue;
                    var reply = OpenAndClose(elm, address);
                    if (reply != null) extra.Add(address.ToString("X2") + ": " + reply);
                }
                summary.Add("6. Other TP2.0 addresses answering: " + (extra.Count == 0 ? "none" : "\r\n     " + string.Join("\r\n     ", extra)));
                found.AddRange(extra.Select(e => Convert.ToInt32(e.Substring(0, 2), 16)));
            }
            else
            {
                summary.Add("6. AIRBAG ANSWERS ON TP2.0 - press Connect, then Read faults / Clear faults.");
            }

            log("=== Test 7: identify each TP2.0 module (read-only) ===");
            var ids = new List<string>();
            foreach (int address in found)
            {
                string id;
                try
                {
                    id = new AirbagModule(elm, log, address).ReadIdentification() ?? "(no identification)";
                }
                catch (System.IO.IOException e)
                {
                    id = "error: " + e.Message;
                }
                ids.Add(address.ToString("X2") + ": " + id + (AirbagConnector.IsAirbag(id) ? "   <== AIRBAG" : ""));
            }
            summary.Add("7. Module identification: " + (ids.Count == 0 ? "none" : "\r\n     " + string.Join("\r\n     ", ids)));
            Try(elm, "ATV0");

            Try(elm, "ATST32");
            return "Bus test results:\r\n\r\n" + string.Join("\r\n", summary) +
                   "\r\n\r\nPlease copy this box and the communication log (click in it, Ctrl+A, Ctrl+C) and send them to me.";
        }

        /// <summary>
        /// Asks one TP2.0 address to open a channel. If it does, completes the handshake and closes it again
        /// properly (so it doesn't keep repeating its answer). Returns the set-up reply, or null.
        /// </summary>
        private static string OpenAndClose(Elm327 elm, int address)
        {
            int replyId = 0x200 + address;
            Try(elm, "ATCF200");
            Try(elm, "ATCM700");
            Try(elm, "ATSH200");
            var reply = Try(elm, address.ToString("X2") + "C00010000301", 2000)
                .Select(l => l.Replace(" ", "")).Where(l => l.Length >= 7 && l.StartsWith(replyId.ToString("X3"))).ToList();
            if (reply.Count == 0) return null;
            var ok = reply.FirstOrDefault(l => l.Length >= 17 && l.Substring(5, 2) == "D0");
            if (ok != null)
            {
                int tx = Convert.ToInt32(ok.Substring(11, 2), 16) | ((Convert.ToInt32(ok.Substring(13, 2), 16) & 0x07) << 8);
                Try(elm, "ATCF300");
                Try(elm, "ATSH" + tx.ToString("X3"));
                Try(elm, "A00F8AFF32FF", 1000);
                Try(elm, "A8", 1000);
            }
            System.Threading.Thread.Sleep(200);
            return reply[0];
        }

        private static bool Answered(List<string> lines, string idPrefix) =>
            lines.Any(l => l.Replace(" ", "").StartsWith(idPrefix));

        private static string Describe(List<string> lines)
        {
            var frames = lines.Where(l => l != "NO DATA" && l != "OK" && l != "?").ToList();
            return frames.Count == 0 ? "no answer" : "ANSWERED: " + string.Join(" | ", frames.Take(6));
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
