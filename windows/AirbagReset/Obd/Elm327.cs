using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AirbagReset.Obd
{
    /// <summary>Byte stream to the adapter (a COM port, or a simulator in tests).</summary>
    public interface IByteChannel
    {
        int BytesAvailable { get; }
        int Read(byte[] buffer, int offset, int count);
        void Write(byte[] data);
    }

    public class ElmException : IOException
    {
        public ElmException(string message) : base(message) { }
    }

    /// <summary>A raw CAN frame as printed by the ELM327 with headers on (ATH1) and auto-formatting off (ATCAF0).</summary>
    public sealed class CanFrame
    {
        public CanFrame(int id, int[] data)
        {
            Id = id;
            Data = data;
        }

        public int Id { get; }
        public int[] Data { get; }

        public override string ToString() =>
            Id.ToString("X3") + " " + string.Join(" ", Data.Select(b => b.ToString("X2")));
    }

    /// <summary>Minimal ELM327 driver.</summary>
    public class Elm327
    {
        // Longest first, so e.g. "SH200" matches "SH" rather than "S".
        private static readonly string[] Mnemonics = { "FCSH", "FCSD", "FCSM", "CAF", "CRA", "CF", "CM", "MA", "V", "SH", "SP", "ST", "AT", "E", "L", "S", "H", "R" };

        private readonly IByteChannel _channel;
        private readonly Action<string> _log;

        public Elm327(IByteChannel channel, Action<string> log = null)
        {
            _channel = channel;
            _log = log ?? (_ => { });
        }

        public string Version { get; private set; } = "unknown";

        /// <summary>Sends a command and returns the non-empty reply lines received before the '>' prompt.</summary>
        public List<string> Command(string cmd, int timeoutMs = 3000)
        {
            Drain();
            _log("> " + cmd);
            _channel.Write(Encoding.ASCII.GetBytes(cmd + "\r"));

            var reply = new StringBuilder();
            var buf = new byte[256];
            var timer = Stopwatch.StartNew();
            while (true)
            {
                int available = _channel.BytesAvailable;
                if (available > 0)
                {
                    int n = _channel.Read(buf, 0, Math.Min(available, buf.Length));
                    string text = Encoding.ASCII.GetString(buf, 0, n);
                    int prompt = text.IndexOf('>');
                    if (prompt >= 0)
                    {
                        reply.Append(text, 0, prompt);
                        break;
                    }
                    reply.Append(text);
                }
                else
                {
                    if (timer.ElapsedMilliseconds > timeoutMs)
                        throw new ElmException("Timed out waiting for the adapter to answer '" + cmd + "'");
                    Thread.Sleep(2);
                }
            }

            var lines = reply.ToString()
                .Split('\r', '\n')
                .Select(l => new string(l.Where(c => c >= ' ').ToArray()).Trim())
                .Where(l => l.Length > 0 && l != cmd && !l.StartsWith("SEARCHING"))
                .ToList();
            foreach (var line in lines) _log("< " + line);
            return lines;
        }

        private void Drain()
        {
            var buf = new byte[256];
            while (_channel.BytesAvailable > 0) _channel.Read(buf, 0, Math.Min(_channel.BytesAvailable, buf.Length));
        }

        private void ExpectOk(string cmd, bool required = true)
        {
            var reply = Command(cmd);
            if (!reply.Any(l => l.Contains("OK")) && reply.Any(l => l.Contains("?")))
            {
                // Some clone firmwares mis-parse commands without spaces (e.g. "ATST19" as "ATS" + "T19").
                // A genuine ELM327 ignores spaces, so the spaced form is always valid to try.
                reply = Command(Spaced(cmd));
            }
            if (!reply.Any(l => l.Contains("OK")))
            {
                string msg = "Adapter rejected " + cmd + " (reply: " + (reply.Count == 0 ? "nothing" : string.Join(" ", reply)) + ")";
                if (required)
                    throw new ElmException(msg + ". This ELM327 clone does not support raw CAN mode, which is needed to talk to VAG modules.");
                _log("Warning: " + msg + " - continuing");
            }
        }

        private static string Spaced(string cmd)
        {
            string body = cmd.StartsWith("AT") ? cmd.Substring(2) : cmd;
            string mnemonic = Mnemonics.FirstOrDefault(m => body.StartsWith(m));
            if (mnemonic == null) return "AT " + body;
            return ("AT " + mnemonic + " " + body.Substring(mnemonic.Length)).Trim();
        }

        /// <summary>Checks the adapter is answering, without changing its settings.</summary>
        public bool Probe()
        {
            // Two tries: the first may be confused by noise left over from opening the port.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (Command("ATI", 1500).Any(l => l.IndexOf("ELM", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                }
                catch (ElmException)
                {
                }
            }
            return false;
        }

        /// <summary>
        /// Resets the adapter and configures it to send/receive raw 11-bit CAN frames at 500 kbit/s,
        /// which is what VW-group TP2.0 diagnostics needs.
        /// </summary>
        public void InitialiseForRawCan()
        {
            var reset = Command("ATZ", 6000);
            Version = reset.FirstOrDefault(l => l.IndexOf("ELM", StringComparison.OrdinalIgnoreCase) >= 0) ?? "unknown";
            ExpectOk("ATE0");           // echo off
            ExpectOk("ATL0", false);    // no linefeeds
            ExpectOk("ATS0", false);    // no spaces
            ExpectOk("ATH1");           // show CAN IDs
            ExpectOk("ATSP6");          // ISO 15765-4 CAN, 11 bit, 500 kbit/s
            ExpectOk("ATAT0", false);   // fixed (not adaptive) timeout
            ExpectOk("ATST19", false);  // reply timeout 25 x 4 ms = 100 ms
            ExpectOk("ATCAF0");         // CAN auto-formatting off: we build TP2.0 frames ourselves
            ExpectOk("ATR1", false);    // wait for responses
        }

        /// <summary>
        /// ATV1: send frames at their real length (VW TP2.0 modules ignore frames padded to 8 bytes).
        /// ATV0: pad to 8 bytes (standard OBD/UDS modules ignore short frames).
        /// </summary>
        public void SetExactFrameLength(bool exact) => ExpectOk(exact ? "ATV1" : "ATV0", false);

        /// <summary>
        /// Switches to ISO-TP mode (ELM handles segmentation and flow control) for UDS modules:
        /// requests go to <paramref name="requestId"/>, replies are accepted from <paramref name="responseId"/>.
        /// </summary>
        public void ConfigureIsoTp(int requestId, int responseId)
        {
            ExpectOk("ATCAF1");
            SetExactFrameLength(false); // UDS modules expect frames padded to 8 bytes
            ExpectOk("ATH0");
            ExpectOk("ATST32", false);  // 200 ms reply timeout
            SetHeader(requestId);
            SetReceiveFilter(responseId);
            // Flow control frames must go to the module's request ID, not the OBD default.
            ExpectOk("ATFCSH" + requestId.ToString("X3"));
            ExpectOk("ATFCSD300000");
            ExpectOk("ATFCSM1");
        }

        /// <summary>Sends an ISO-TP request (auto-formatting on, headers off) and returns every reassembled reply.</summary>
        public List<int[]> SendIsoTp(int[] data)
        {
            string hex = string.Concat(data.Select(b => b.ToString("X2")));
            var messages = new List<int[]>();
            List<int> multi = null;
            int multiLength = 0;
            foreach (var line in Command(hex, 5000))
            {
                string compact = line.Replace(" ", "");
                if (compact == "NODATA" || compact == "OK" || compact.Length == 0)
                    continue;
                if (compact.Length == 3 && compact.All(IsHex))
                {
                    // Multi-frame reply: total length, then lines "0:..." "1:..."
                    multiLength = Convert.ToInt32(compact, 16);
                    multi = new List<int>();
                    continue;
                }
                int colon = compact.IndexOf(':');
                if (colon > 0 && multi != null)
                {
                    multi.AddRange(ParseHex(compact.Substring(colon + 1)));
                    if (multi.Count >= multiLength)
                    {
                        messages.Add(multi.Take(multiLength).ToArray());
                        multi = null;
                    }
                    continue;
                }
                if (compact.Length % 2 == 0 && compact.All(IsHex))
                {
                    messages.Add(ParseHex(compact));
                    continue;
                }
                if (compact == "?")
                    throw new ElmException("Adapter did not understand request " + hex);
                if (compact.Contains("ERROR") || compact.Contains("UNABLETOCONNECT"))
                    throw new ElmException("CAN bus problem: " + line + ". Is the ignition on?");
                _log("Ignoring unexpected adapter output: " + line);
            }
            if (multi != null && multi.Count > 0) messages.Add(multi.ToArray());
            return messages;
        }

        private static int[] ParseHex(string hex)
        {
            var bytes = new int[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToInt32(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        /// <summary>Listens to all CAN traffic (ATMA) for a while without sending anything; returns the lines seen.</summary>
        public List<string> Monitor(int durationMs, int maxLines = 40)
        {
            Drain();
            _log("> ATMA (listening for " + durationMs + " ms)");
            _channel.Write(Encoding.ASCII.GetBytes("ATMA\r"));
            Thread.Sleep(durationMs);
            _channel.Write(Encoding.ASCII.GetBytes("\r")); // any character stops monitoring
            var reply = new StringBuilder();
            var buf = new byte[1024];
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 3000)
            {
                int available = _channel.BytesAvailable;
                if (available > 0)
                {
                    int n = _channel.Read(buf, 0, Math.Min(available, buf.Length));
                    string text = Encoding.ASCII.GetString(buf, 0, n);
                    reply.Append(text);
                    if (text.Contains(">")) break;
                }
                else Thread.Sleep(5);
            }
            var lines = reply.ToString().Replace(">", "").Split('\r', '\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && l != "ATMA").ToList();
            foreach (var line in lines.Take(maxLines)) _log("< " + line);
            if (lines.Count > maxLines) _log("< ... " + (lines.Count - maxLines) + " more lines");
            return lines;
        }

        public void SetHeader(int id) => ExpectOk("ATSH" + id.ToString("X3"));

        /// <summary>
        /// Only accept replies from <paramref name="id"/>. Uses ATCF + ATCM rather than ATCRA: some clones
        /// accept ATCRA but leave their default 7xx-only filter in place, which silently drops TP2.0 replies (2xx/3xx).
        /// </summary>
        public void SetReceiveFilter(int id) => SetReceiveFilter(id, 0x7FF);

        /// <summary>Accept IDs where (id &amp; mask) == (filter &amp; mask). Mask 000 accepts everything.</summary>
        public void SetReceiveFilter(int filter, int mask)
        {
            ExpectOk("ATCF" + filter.ToString("X3"));
            ExpectOk("ATCM" + mask.ToString("X3"));
        }

        /// <summary>Transmits one CAN frame and returns every frame received before the adapter timed out.</summary>
        public List<CanFrame> SendFrame(int[] data)
        {
            string hex = string.Concat(data.Select(b => b.ToString("X2")));
            var frames = new List<CanFrame>();
            foreach (var line in Command(hex))
            {
                string compact = line.Replace(" ", "");
                if (compact == "NODATA" || compact == "OK")
                    continue;
                if (compact.Length >= 3 && compact.Length % 2 == 1 && compact.All(IsHex))
                {
                    int id = Convert.ToInt32(compact.Substring(0, 3), 16);
                    var bytes = new int[(compact.Length - 3) / 2];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToInt32(compact.Substring(3 + i * 2, 2), 16);
                    frames.Add(new CanFrame(id, bytes));
                }
                else if (compact == "?")
                    throw new ElmException("Adapter did not understand frame " + hex);
                else if (compact.Contains("ERROR") || compact.Contains("UNABLETOCONNECT"))
                    throw new ElmException("CAN bus problem: " + line + ". Is the ignition on?");
                else if (compact.Contains("BUFFERFULL"))
                    throw new ElmException("Adapter buffer overflow");
                else
                    _log("Ignoring unexpected adapter output: " + line);
            }
            return frames;
        }

        private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
    }
}
