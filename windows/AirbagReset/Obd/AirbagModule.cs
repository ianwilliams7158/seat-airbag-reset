using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AirbagReset.Obd
{
    public sealed class Dtc
    {
        private static readonly Dictionary<int, string> Descriptions = new Dictionary<int, string>
        {
            { 532, "Supply Voltage B+" },
            { 588, "Airbag Igniter; Driver Side (N95)" },
            { 589, "Airbag Igniter; Passenger Side (N131)" },
            { 595, "Crash Data Stored - module must be replaced/reset, cannot be cleared" },
            { 1217, "Side Airbag Igniter; Driver Side (N199)" },
            { 1218, "Side Airbag Igniter; Passenger Side (N200)" },
            { 1221, "Side Airbag Crash Sensor; Driver Side (G179)" },
            { 1222, "Side Airbag Crash Sensor; Passenger Side (G180)" },
            { 1280, "Airbag Deactivated; Passenger Side (check glovebox key switch E224)" },
            { 1299, "Diagnostic Interface for Data Bus (J533)" },
        };

        public Dtc(int code, int status, bool uds = false)
        {
            Code = code;
            Status = status;
            IsUds = uds;
        }

        /// <summary>Fault code number: 5-digit VAG code (KWP) or 24-bit DTC (UDS).</summary>
        public int Code { get; }
        public int Status { get; }
        public bool IsUds { get; }

        /// <summary>For UDS faults, whether the fault is present right now (status bit 0 "test failed").</summary>
        public bool IsActive => IsUds && (Status & 0x01) != 0;

        public string CodeText => IsUds ? UdsText() : Code.ToString("D5");

        public string Description
        {
            get
            {
                if (IsUds) return (IsActive ? "Present now" : "Stored (not currently present)") + " - search \"" + CodeText + " airbag\" for details";
                return Descriptions.TryGetValue(Code, out var d) ? d : "Unknown code - search \"VAG " + CodeText + "\"";
            }
        }

        // 3-byte UDS DTC: 2 bytes SAE code (letter P/C/B/U + 4 digits) + 1 byte failure type.
        private string UdsText()
        {
            int sae = Code >> 8;
            char letter = "PCBU"[(sae >> 14) & 0x3];
            return letter + ((sae >> 12) & 0x3).ToString() + (sae & 0xFFF).ToString("X3") + " " + (Code & 0xFF).ToString("X2");
        }

        public override string ToString() => CodeText + "  " + Description + "  (status " + Status.ToString("X2") + ")";
    }

    public class ModuleException : IOException
    {
        public ModuleException(string message) : base(message) { }
    }

    /// <summary>Fault memory access to the airbag control unit, whichever protocol it speaks.</summary>
    public interface IAirbagModule
    {
        string Protocol { get; }
        string ReadIdentification();
        List<Dtc> ReadFaults();
        /// <summary>Clears all stored faults and returns whatever is still stored afterwards.</summary>
        List<Dtc> ClearFaults();
    }

    public static class AirbagConnector
    {
        // The VAG "15 - Airbag" number is not always the module's TP2.0 address, so after 0x15 try the
        // addresses that answered on the user's car, then other common ones. Each is identified before use.
        private static readonly int[] Tp20Candidates = { 0x15, 0x05, 0x1D, 0x1F, 0x20, 0x33, 0x54, 0x07, 0x0B, 0x0D, 0x11, 0x13 };

        /// <summary>True if a module identification string belongs to an airbag control unit.</summary>
        public static bool IsAirbag(string identification) =>
            identification != null &&
            (identification.IndexOf("AIRBAG", StringComparison.OrdinalIgnoreCase) >= 0 ||
             identification.Contains("909605") || identification.Contains("959655") || identification.Contains("959651"));

        /// <summary>
        /// Finds the airbag module. Older PQ35 airbag units use VW TP2.0 + KWP2000; later ones use UDS on
        /// CAN IDs 715/77F. Only a module that identifies itself as an airbag unit is used.
        /// </summary>
        public static IAirbagModule Connect(Elm327 elm, Action<string> log, out string identification)
        {
            var seen = new List<string>();
            log("Looking for the airbag module on VW TP2.0...");
            foreach (int address in Tp20Candidates)
            {
                var module = new AirbagModule(elm, log, address);
                string id;
                try
                {
                    id = module.ReadIdentification();
                }
                catch (IOException e)
                {
                    log("TP2.0 address " + address.ToString("X2") + ": " + e.Message);
                    continue;
                }
                log("TP2.0 address " + address.ToString("X2") + " is: " + (id ?? "(no identification)"));
                seen.Add(address.ToString("X2") + " = " + (id ?? "?"));
                if (IsAirbag(id))
                {
                    identification = id;
                    return module;
                }
            }

            log("Trying UDS (2009+ airbag units)...");
            var uds = new UdsAirbagModule(elm, log);
            try
            {
                identification = uds.ReadIdentification();
                return uds;
            }
            catch (ModuleException e)
            {
                log("UDS: " + e.Message);
            }
            throw new ModuleException("Could not find the airbag module." +
                (seen.Count > 0 ? " Modules found: " + string.Join("; ", seen) + "." : "") +
                " Check the ignition is ON (engine off).");
        }
    }

    /// <summary>KWP2000 over TP2.0 access to the airbag control unit (VAG address 15).</summary>
    public class AirbagModule : IAirbagModule
    {
        public const int Address = 0x15;

        private readonly Elm327 _elm;
        private readonly Action<string> _log;

        public AirbagModule(Elm327 elm, Action<string> log = null, int address = Address)
        {
            _elm = elm;
            _log = log ?? (_ => { });
            ModuleAddress = address;
        }

        /// <summary>TP2.0 logical address actually used to reach the module.</summary>
        public int ModuleAddress { get; }

        public string Protocol => "VW TP2.0 / KWP2000, address " + ModuleAddress.ToString("X2");

        private T WithChannel<T>(Func<Tp20Channel, T> block)
        {
            var channel = new Tp20Channel(_elm, _log);
            channel.Open(ModuleAddress);
            try
            {
                var session = channel.Request(new[] { 0x10, 0x89 });
                if (session.FirstOrDefault() != 0x50)
                    _log("Diagnostic session not accepted (" + Hex(session) + "), continuing anyway");
                return block(channel);
            }
            finally
            {
                channel.Close();
            }
        }

        /// <summary>Returns the module part number / name, e.g. "1P0909605D  AIRBAG VW10", or null if unavailable.</summary>
        public string ReadIdentification() => WithChannel(channel =>
        {
            var reply = channel.Request(new[] { 0x1A, 0x9B });
            if (reply.FirstOrDefault() != 0x5A) return null;
            var text = new string(reply.Skip(2).Where(b => b >= 0x20 && b <= 0x7E).Select(b => (char)b).ToArray()).Trim();
            return text.Length == 0 ? null : text;
        });

        public List<Dtc> ReadFaults() => WithChannel(ReadFaults);

        /// <summary>Clears all stored faults and returns whatever is still stored afterwards.</summary>
        public List<Dtc> ClearFaults() => WithChannel(channel =>
        {
            var reply = channel.Request(new[] { 0x14, 0xFF, 0x00 });
            if (reply.FirstOrDefault() != 0x54) throw new ModuleException("Module refused to clear faults: " + Negative(reply));
            _log("Clear command accepted");
            return ReadFaults(channel);
        });

        private List<Dtc> ReadFaults(Tp20Channel channel)
        {
            var reply = channel.Request(new[] { 0x18, 0x02, 0xFF, 0x00 });
            if (reply.FirstOrDefault() != 0x58) throw new ModuleException("Module refused to report faults: " + Negative(reply));
            var faults = new List<Dtc>();
            for (int i = 2; i + 2 < reply.Length; i += 3)
            {
                int code = (reply[i] << 8) | reply[i + 1];
                if (code != 0xFFFF) faults.Add(new Dtc(code, reply[i + 2]));
            }
            return faults;
        }

        private static string Negative(int[] reply) =>
            reply.Length >= 3 && reply[0] == 0x7F ? "negative response code " + reply[2].ToString("X2") : Hex(reply);

        private static string Hex(int[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>UDS (ISO 14229) over ISO-TP access to 2009+ VW-group airbag units: request 0x715, reply 0x77F.</summary>
    public class UdsAirbagModule : IAirbagModule
    {
        public const int RequestId = 0x715;
        public const int ResponseId = 0x77F;

        private readonly Elm327 _elm;
        private readonly Action<string> _log;
        private bool _configured;

        public UdsAirbagModule(Elm327 elm, Action<string> log = null)
        {
            _elm = elm;
            _log = log ?? (_ => { });
        }

        public string Protocol => "UDS (CAN 715/77F)";

        private int[] Request(params int[] request)
        {
            if (!_configured)
            {
                _elm.ConfigureIsoTp(RequestId, ResponseId);
                _configured = true;
            }
            bool sawPending = false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                foreach (var msg in _elm.SendIsoTp(request))
                {
                    if (msg.Length >= 3 && msg[0] == 0x7F && msg[1] == request[0] && msg[2] == 0x78)
                    {
                        _log("Module busy, waiting...");
                        sawPending = true;
                        continue;
                    }
                    if (msg.Length > 0 && (msg[0] == request[0] + 0x40 || (msg[0] == 0x7F && msg.Length >= 2 && msg[1] == request[0])))
                        return msg;
                }
                if (!sawPending) break;
            }
            throw new ModuleException("No answer from the airbag module (UDS address " + RequestId.ToString("X3") + ")");
        }

        private void StartExtendedSession()
        {
            var reply = Request(0x10, 0x03);
            if (reply[0] != 0x50) _log("Extended session not accepted (" + Hex(reply) + "), continuing in default session");
        }

        public string ReadIdentification()
        {
            var parts = new List<string>();
            foreach (int did in new[] { 0xF187, 0xF197 }) // VW spare part number, system name
            {
                var reply = Request(0x22, did >> 8, did & 0xFF);
                if (reply[0] == 0x62 && reply.Length > 3)
                    parts.Add(new string(reply.Skip(3).Where(b => b >= 0x20 && b <= 0x7E).Select(b => (char)b).ToArray()).Trim());
            }
            var text = string.Join("  ", parts.Where(p => p.Length > 0));
            return text.Length == 0 ? null : text;
        }

        public List<Dtc> ReadFaults()
        {
            var reply = Request(0x19, 0x02, 0xFF); // report DTCs by status mask: all
            if (reply[0] != 0x59) throw new ModuleException("Module refused to report faults: " + Negative(reply));
            var faults = new List<Dtc>();
            for (int i = 3; i + 3 < reply.Length; i += 4)
            {
                int dtc = (reply[i] << 16) | (reply[i + 1] << 8) | reply[i + 2];
                faults.Add(new Dtc(dtc, reply[i + 3], uds: true));
            }
            return faults;
        }

        public List<Dtc> ClearFaults()
        {
            StartExtendedSession();
            var reply = Request(0x14, 0xFF, 0xFF, 0xFF);
            if (reply[0] != 0x54) throw new ModuleException("Module refused to clear faults: " + Negative(reply));
            _log("Clear command accepted");
            return ReadFaults();
        }

        private static string Negative(int[] reply) =>
            reply.Length >= 3 && reply[0] == 0x7F ? "negative response code " + reply[2].ToString("X2") : Hex(reply);

        private static string Hex(int[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));
    }
}
