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

        public Dtc(int code, int status)
        {
            Code = code;
            Status = status;
        }

        public int Code { get; }
        public int Status { get; }
        public string CodeText => Code.ToString("D5");

        public string Description =>
            Descriptions.TryGetValue(Code, out var d) ? d : "Unknown code - search \"VAG " + CodeText + "\"";

        public override string ToString() => CodeText + "  " + Description + "  (status " + Status.ToString("X2") + ")";
    }

    public class ModuleException : IOException
    {
        public ModuleException(string message) : base(message) { }
    }

    /// <summary>KWP2000 access to the airbag control unit (VAG address 15).</summary>
    public class AirbagModule
    {
        public const int Address = 0x15;

        private readonly Elm327 _elm;
        private readonly Action<string> _log;

        public AirbagModule(Elm327 elm, Action<string> log = null)
        {
            _elm = elm;
            _log = log ?? (_ => { });
        }

        private T WithChannel<T>(Func<Tp20Channel, T> block)
        {
            var channel = new Tp20Channel(_elm, _log);
            channel.Open(Address);
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
}
