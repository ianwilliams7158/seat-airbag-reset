using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AirbagReset.Obd;
using Xunit;

namespace AirbagReset.Tests
{
    /// <summary>
    /// Simulates an ELM327 in raw CAN mode connected to a PQ35 airbag module speaking TP2.0 + KWP2000.
    /// Single-threaded: each command written produces its full reply (ending in '>') immediately.
    /// </summary>
    internal sealed class FakeElmWithAirbagEcu : IByteChannel
    {
        private readonly StringBuilder _out = new StringBuilder();
        private readonly StringBuilder _line = new StringBuilder();
        private readonly List<int> _rx = new List<int>();
        private readonly List<string> _pendingFrames = new List<string>();
        private readonly bool _faultPresent;
        private readonly bool _clearWithPending;
        private readonly bool _noRawCan;
        private readonly bool _needsSpaces;
        private int _header = 0x7DF;
        private int _filter;
        private int _ecuSeq;
        private int _rxLen = -1;

        public FakeElmWithAirbagEcu(List<Dtc> faults, bool faultPresent = false, bool clearWithPending = true,
            bool noRawCan = false, bool needsSpaces = false)
        {
            Faults = faults;
            _faultPresent = faultPresent;
            _clearWithPending = clearWithPending;
            _noRawCan = noRawCan;
            _needsSpaces = needsSpaces;
        }

        public List<Dtc> Faults { get; private set; }
        public int ClearRequests { get; private set; }
        public List<string> SentFrames { get; } = new List<string>();

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
                if (b == '\r')
                {
                    Handle(_line.ToString());
                    _line.Clear();
                }
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
            if (_needsSpaces && (raw == "ATST19" || raw == "ATCAF0")) { Reply(new[] { "?" }); return; }
            string cmd = raw.Replace(" ", "");
            if (cmd.StartsWith("AT"))
            {
                if (cmd == "ATZ") Reply(new[] { "ELM327 v1.5" });
                else if (cmd == "ATI") Reply(new[] { "ELM327 v1.5" });
                else if (cmd == "ATCAF0" && _noRawCan) Reply(new[] { "?" });
                else if (cmd.StartsWith("ATSH")) { _header = Convert.ToInt32(cmd.Substring(4), 16); Reply(new[] { "OK" }); }
                else if (cmd.StartsWith("ATCRA")) { _filter = Convert.ToInt32(cmd.Substring(5), 16); Reply(new[] { "OK" }); }
                else Reply(new[] { "OK" });
                return;
            }
            SentFrames.Add(_header.ToString("X3") + ":" + cmd);
            var d = Enumerable.Range(0, cmd.Length / 2).Select(i => Convert.ToInt32(cmd.Substring(i * 2, 2), 16)).ToArray();
            var output = new List<string>();
            if (_header == 0x200 && d[0] == 0x15 && d[1] == 0xC0) output.Add("21500D00003400701");
            else if (_header == 0x740 && d[0] == 0xA0) output.Add("300A10F8AFF4AFF");
            else if (_header == 0x740 && d[0] == 0xA3) { output.Add("300A10F8AFF4AFF"); output.AddRange(_pendingFrames); _pendingFrames.Clear(); }
            else if (_header == 0x740 && (d[0] == 0xA8 || d[0] >> 4 == 0xB)) { }
            else if (_header == 0x740 && d[0] >> 4 <= 0x3)
            {
                int seq = d[0] & 0xF;
                if (_rxLen < 0) { _rxLen = (d[1] << 8) | d[2]; _rx.AddRange(d.Skip(3)); }
                else _rx.AddRange(d.Skip(1));
                if (d[0] >> 4 <= 1) output.Add("300B" + ((seq + 1) & 0xF).ToString("X"));
                if (_rx.Count >= _rxLen)
                {
                    var req = _rx.Take(_rxLen).ToList();
                    _rx.Clear();
                    _rxLen = -1;
                    output.AddRange(Frames(Kwp(req)));
                }
            }
            var visible = output.Where(l => Convert.ToInt32(l.Substring(0, 3), 16) == _filter).ToList();
            Reply(visible.Count > 0 ? visible : new List<string> { "NO DATA" });
        }

        private List<int> Kwp(List<int> req)
        {
            switch (req[0])
            {
                case 0x10: return new List<int> { 0x50, req[1] };
                case 0x1A: return new List<int> { 0x5A, 0x9B }.Concat("1P0909605D  AIRBAG VW10".Select(c => (int)c)).ToList();
                case 0x18:
                    return new List<int> { 0x58, Faults.Count }
                        .Concat(Faults.SelectMany(f => new[] { f.Code >> 8, f.Code & 0xFF, f.Status })).ToList();
                case 0x14:
                    ClearRequests++;
                    Faults = _faultPresent ? Faults.Take(1).ToList() : new List<Dtc>();
                    if (_clearWithPending)
                    {
                        _pendingFrames.AddRange(Frames(new List<int> { 0x54, 0xFF, 0x00 }));
                        return new List<int> { 0x7F, 0x14, 0x78 };
                    }
                    return new List<int> { 0x54, 0xFF, 0x00 };
                default: return new List<int> { 0x7F, req[0], 0x11 };
            }
        }

        private List<string> Frames(List<int> payload)
        {
            var full = new List<int> { payload.Count >> 8, payload.Count & 0xFF };
            full.AddRange(payload);
            var frames = new List<string>();
            for (int i = 0; i < full.Count; i += 7)
            {
                bool last = i + 7 >= full.Count;
                int b0 = (last ? 0x10 : 0x20) | _ecuSeq;
                _ecuSeq = (_ecuSeq + 1) & 0xF;
                frames.Add("300" + string.Concat(new[] { b0 }.Concat(full.Skip(i).Take(7)).Select(b => b.ToString("X2"))));
            }
            return frames;
        }
    }

    public class AirbagModuleTests
    {
        private static AirbagModule Connect(FakeElmWithAirbagEcu fake)
        {
            var elm = new Elm327(fake, Console.WriteLine);
            Assert.True(elm.Probe());
            elm.InitialiseForRawCan();
            Assert.Equal("ELM327 v1.5", elm.Version);
            return new AirbagModule(elm, Console.WriteLine);
        }

        [Fact]
        public void ReadsIdentification()
        {
            Assert.Equal("1P0909605D  AIRBAG VW10", Connect(new FakeElmWithAirbagEcu(new List<Dtc>())).ReadIdentification());
        }

        [Fact]
        public void ReadsMultiFrameFaultList()
        {
            var fake = new FakeElmWithAirbagEcu(new List<Dtc> { new Dtc(1280, 0x23), new Dtc(589, 0x24), new Dtc(532, 0x08) });
            var faults = Connect(fake).ReadFaults();
            Assert.Equal(new[] { 1280, 589, 532 }, faults.Select(f => f.Code));
            Assert.Equal("01280", faults[0].CodeText);
            Assert.Equal(0x24, faults[1].Status);
            Assert.Contains("200:15C00010000301", fake.SentFrames);
            Assert.Contains(fake.SentFrames, f => f.StartsWith("740:B"));
        }

        [Fact]
        public void ClearsFaultsThroughResponsePending()
        {
            var fake = new FakeElmWithAirbagEcu(new List<Dtc> { new Dtc(1280, 0x23) });
            Assert.Empty(Connect(fake).ClearFaults());
            Assert.Equal(1, fake.ClearRequests);
        }

        [Fact]
        public void ReportsFaultThatIsStillPresent()
        {
            var fake = new FakeElmWithAirbagEcu(new List<Dtc> { new Dtc(589, 0x24) }, faultPresent: true, clearWithPending: false);
            Assert.Equal(new[] { 589 }, Connect(fake).ClearFaults().Select(f => f.Code));
        }

        [Fact]
        public void RetriesCommandsWithSpacesForPickyClones()
        {
            var fake = new FakeElmWithAirbagEcu(new List<Dtc> { new Dtc(1280, 0x23) }, needsSpaces: true);
            Assert.Equal(new[] { 1280 }, Connect(fake).ReadFaults().Select(f => f.Code));
        }

        [Fact]
        public void RejectsAdapterWithoutRawCan()
        {
            Assert.Throws<ElmException>(() => Connect(new FakeElmWithAirbagEcu(new List<Dtc>(), noRawCan: true)));
        }
    }
}
