using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AirbagReset.Obd;
using Xunit;

namespace AirbagReset.Tests
{
    /// <summary>
    /// Simulates an ELM327 connected to a 2009+ (UDS) airbag unit that ignores TP2.0 requests.
    /// Mimics the ELM327's ISO-TP output with auto-formatting on and headers off.
    /// </summary>
    internal sealed class FakeElmWithUdsAirbag : IByteChannel
    {
        private readonly StringBuilder _out = new StringBuilder();
        private readonly StringBuilder _line = new StringBuilder();
        private readonly bool _faultPresent;
        private readonly bool _pendingOnly;
        private bool _caf = true;
        private int _header = 0x7DF;
        private int _filter = -1; // no filter after reset, like a real ELM327
        private int _flowControlHeader = -1;
        private bool _pendingSent;

        public FakeElmWithUdsAirbag(List<(int dtc, int status)> faults, bool faultPresent = false, bool pendingOnly = false)
        {
            Faults = faults;
            _faultPresent = faultPresent;
            _pendingOnly = pendingOnly;
        }

        public List<(int dtc, int status)> Faults { get; private set; }
        public int ClearRequests { get; private set; }

        public int BytesAvailable => _out.Length;

        public int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _out.Length);
            Encoding.ASCII.GetBytes(_out.ToString(0, n), 0, n, buffer, offset);
            _out.Remove(0, n);
            return n;
        }

        public void Write(byte[] data)
        {
            foreach (byte b in data)
            {
                if (b == '\r') { Handle(_line.ToString()); _line.Clear(); }
                else _line.Append((char)b);
            }
        }

        private void Reply(IEnumerable<string> lines)
        {
            foreach (var l in lines) _out.Append(l).Append('\r');
            _out.Append("\r>");
        }

        private void Handle(string raw)
        {
            string cmd = raw.Replace(" ", "");
            if (cmd.Length == 0) { Reply(new string[0]); return; } // stops ATMA
            if (cmd.StartsWith("AT"))
            {
                if (cmd == "ATZ" || cmd == "ATI") Reply(new[] { "ELM327 v1.5" });
                else if (cmd.StartsWith("ATCAF")) { _caf = cmd.EndsWith("1"); Reply(new[] { "OK" }); }
                else if (cmd.StartsWith("ATSH")) { _header = Convert.ToInt32(cmd.Substring(4), 16); Reply(new[] { "OK" }); }
                else if (cmd.StartsWith("ATCF")) { _filter = Convert.ToInt32(cmd.Substring(4), 16); Reply(new[] { "OK" }); }
                else if (cmd.StartsWith("ATCM")) { if (cmd.Substring(4) == "000") _filter = -1; Reply(new[] { "OK" }); }
                else if (cmd.StartsWith("ATCRA")) { _filter = cmd.Length > 5 ? Convert.ToInt32(cmd.Substring(5), 16) : -1; Reply(new[] { "OK" }); }
                else if (cmd.StartsWith("ATFCSH")) { _flowControlHeader = Convert.ToInt32(cmd.Substring(6), 16); Reply(new[] { "OK" }); }
                else Reply(new[] { "OK" });
                return;
            }
            // Only a correctly configured ISO-TP request to 715 gets an answer.
            if (!_caf || _header != 0x715 || (_filter != 0x77F && _filter != -1)) { Reply(new[] { "NO DATA" }); return; }
            var req = Enumerable.Range(0, cmd.Length / 2).Select(i => Convert.ToInt32(cmd.Substring(i * 2, 2), 16)).ToList();
            var replies = Uds(req);
            var lines = new List<string>();
            foreach (var r in replies)
            {
                if (r.Count > 7 && _flowControlHeader != 0x715) continue; // module never sees our flow control
                lines.AddRange(Format(r));
            }
            Reply(lines.Count > 0 ? lines : new List<string> { "NO DATA" });
        }

        private List<List<int>> Uds(List<int> req)
        {
            switch (req[0])
            {
                case 0x10: return new List<List<int>> { new List<int> { 0x50, req[1], 0x00, 0x32, 0x01, 0xF4 } };
                case 0x22:
                    string text = req[2] == 0x87 ? "1P0959655D " : "AirbagVW31";
                    return new List<List<int>> { new List<int> { 0x62, req[1], req[2] }.Concat(text.Select(c => (int)c)).ToList() };
                case 0x19:
                    return new List<List<int>> {
                        new List<int> { 0x59, 0x02, 0xFF }.Concat(Faults.SelectMany(f => new[] { f.dtc >> 16, (f.dtc >> 8) & 0xFF, f.dtc & 0xFF, f.status })).ToList() };
                case 0x14:
                    ClearRequests++;
                    Faults = _faultPresent ? Faults.Take(1).ToList() : new List<(int, int)>();
                    var pending = new List<int> { 0x7F, 0x14, 0x78 };
                    if (_pendingOnly && !_pendingSent) { _pendingSent = true; return new List<List<int>> { pending }; }
                    return new List<List<int>> { pending, new List<int> { 0x54 } };
                default: return new List<List<int>> { new List<int> { 0x7F, req[0], 0x11 } };
            }
        }

        private static IEnumerable<string> Format(List<int> msg)
        {
            string Hex(IEnumerable<int> b) => string.Concat(b.Select(x => x.ToString("X2")));
            if (msg.Count <= 7) { yield return Hex(msg); yield break; }
            yield return msg.Count.ToString("X3");
            yield return "0:" + Hex(msg.Take(6));
            int n = 1;
            for (int i = 6; i < msg.Count; i += 7, n++) yield return (n % 16).ToString("X") + ":" + Hex(msg.Skip(i).Take(7));
        }
    }

    public class UdsAirbagModuleTests
    {
        private static IAirbagModule Connect(FakeElmWithUdsAirbag fake, out string id)
        {
            var elm = new Elm327(fake, Console.WriteLine);
            Assert.True(elm.Probe());
            elm.InitialiseForRawCan();
            return AirbagConnector.Connect(elm, Console.WriteLine, out id);
        }

        [Fact]
        public void FallsBackToUdsWhenTp20GetsNoAnswer()
        {
            var module = Connect(new FakeElmWithUdsAirbag(new List<(int, int)>()), out var id);
            Assert.IsType<UdsAirbagModule>(module);
            Assert.Equal("1P0959655D  AirbagVW31", id);
        }

        [Fact]
        public void ReadsMultiFrameUdsFaults()
        {
            var fake = new FakeElmWithUdsAirbag(new List<(int, int)> { (0x9A1013, 0x09), (0x934000, 0x08) });
            var faults = Connect(fake, out _).ReadFaults();
            Assert.Equal(new[] { 0x9A1013, 0x934000 }, faults.Select(f => f.Code));
            Assert.Equal("B1A10 13", faults[0].CodeText);
            Assert.True(faults[0].IsActive);
            Assert.False(faults[1].IsActive);
        }

        [Fact]
        public void ClearsUdsFaults()
        {
            var fake = new FakeElmWithUdsAirbag(new List<(int, int)> { (0x9A1013, 0x08) });
            Assert.Empty(Connect(fake, out _).ClearFaults());
            Assert.Equal(1, fake.ClearRequests);
        }

        [Fact]
        public void ResendsClearWhenOnlyResponsePendingArrives()
        {
            var fake = new FakeElmWithUdsAirbag(new List<(int, int)> { (0x9A1013, 0x08) }, pendingOnly: true);
            Assert.Empty(Connect(fake, out _).ClearFaults());
        }

        [Fact]
        public void ReportsUdsFaultStillPresent()
        {
            var fake = new FakeElmWithUdsAirbag(new List<(int, int)> { (0x9A1013, 0x09) }, faultPresent: true);
            var remaining = Connect(fake, out _).ClearFaults();
            Assert.Single(remaining);
            Assert.True(remaining[0].IsActive);
        }
    }
}

namespace AirbagReset.Tests
{
    public class BusTestTests
    {
        [Fact]
        public void FindsTp20AirbagWithOpenFilter()
        {
            var fake = new FakeElmWithAirbagEcu(new System.Collections.Generic.List<Dtc>());
            var summary = BusTest.Run(new Elm327(fake, System.Console.WriteLine), System.Console.WriteLine);
            Assert.Contains("Airbag (15): 21500D00003400701", summary);
            Assert.Contains("6. AIRBAG ANSWERS ON TP2.0", summary);
        }

        [Fact]
        public void RunsAgainstSilentBusWithoutThrowing()
        {
            var fake = new FakeElmWithUdsAirbag(new System.Collections.Generic.List<(int, int)>());
            var summary = BusTest.Run(new Elm327(fake, System.Console.WriteLine), System.Console.WriteLine);
            Assert.Contains("1. Engine standard OBD: no answer", summary);
            Assert.Contains("5. TP2.0 modules answering: none", summary);
            Assert.Contains("6. Other TP2.0 addresses answering: none", summary);
        }
    }
}
