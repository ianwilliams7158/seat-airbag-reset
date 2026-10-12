using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AirbagReset.Obd
{
    public class Tp20Exception : IOException
    {
        public Tp20Exception(string message) : base(message) { }
    }

    /// <summary>
    /// VW "Transport Protocol 2.0" over CAN, as used by PQ35 vehicles (Seat Leon 1P, Golf 5/6, A3 8P...)
    /// for KWP2000 diagnostics. Implemented on top of an ELM327 in raw CAN mode.
    ///
    /// Data frame byte 0: high nibble = opcode, low nibble = sequence number.
    ///   0x0 / 0x1 = more follow / last frame, receiver must ACK
    ///   0x2 / 0x3 = more follow / last frame, no ACK expected
    ///   0xB / 0x9 = ACK (ready / not ready) carrying the next expected sequence number
    ///   0xA0..0xA8 = channel parameters, keep-alive, disconnect
    /// </summary>
    public class Tp20Channel
    {
        private readonly Elm327 _elm;
        private readonly Action<string> _log;
        private readonly Queue<int[]> _messages = new Queue<int[]>();
        private int _txSeq;
        private int _lastRxSeq = -1;
        private List<int> _assembling;
        private int _expectedLength;

        public Tp20Channel(Elm327 elm, Action<string> log = null)
        {
            _elm = elm;
            _log = log ?? (_ => { });
        }

        public bool IsOpen { get; private set; }

        public void Open(int moduleAddress)
        {
            _elm.SetHeader(0x200);
            _elm.SetReceiveFilter(0x200 + moduleAddress);
            // Ask the module to open a channel: we will listen on 0x300, the module picks its own ID.
            var replies = _elm.SendFrame(new[] { moduleAddress, 0xC0, 0x00, 0x10, 0x00, 0x03, 0x01 });
            var setup = replies.FirstOrDefault(f => f.Data.Length >= 7 && f.Data[1] == 0xD0);
            if (setup == null)
            {
                var refusal = replies.FirstOrDefault(f => f.Data.Length >= 2 && f.Data[1] >= 0xD6 && f.Data[1] <= 0xD8);
                if (refusal != null)
                    throw new Tp20Exception(string.Format("Module 0x{0:X2} refused the diagnostic channel (code {1:X2})", moduleAddress, refusal.Data[1]));
                throw new Tp20Exception(string.Format("No answer from module 0x{0:X2}. Check the ignition is ON and the adapter is plugged in.", moduleAddress));
            }
            int rxId = setup.Data[2] | ((setup.Data[3] & 0x07) << 8);
            int txId = setup.Data[4] | ((setup.Data[5] & 0x07) << 8);
            _log(string.Format("Channel open: tester sends on {0:X3}, module replies on {1:X3}", txId, rxId));

            _elm.SetHeader(txId);
            _elm.SetReceiveFilter(rxId);
            var parameters = _elm.SendFrame(new[] { 0xA0, 0x0F, 0x8A, 0xFF, 0x32, 0xFF });
            if (!parameters.Any(f => f.Data.Length > 0 && f.Data[0] == 0xA1))
                throw new Tp20Exception("Module did not accept channel timing parameters");
            _txSeq = 0;
            _lastRxSeq = -1;
            _assembling = null;
            _messages.Clear();
            IsOpen = true;
        }

        /// <summary>Sends a KWP2000 request and returns the final reply (skipping "response pending" replies).</summary>
        public int[] Request(int[] payload, int timeoutMs = 8000)
        {
            if (!IsOpen) throw new InvalidOperationException("Channel not open");
            _messages.Clear();
            var full = new List<int> { (payload.Length >> 8) & 0xFF, payload.Length & 0xFF };
            full.AddRange(payload);
            var chunks = new List<List<int>>();
            for (int i = 0; i < full.Count; i += 7) chunks.Add(full.Skip(i).Take(7).ToList());
            for (int i = 0; i < chunks.Count; i++)
            {
                int opcode = i == chunks.Count - 1 ? 0x10 : 0x20;
                var frame = new List<int> { opcode | _txSeq };
                frame.AddRange(chunks[i]);
                _txSeq = (_txSeq + 1) & 0x0F;
                Process(_elm.SendFrame(frame.ToArray()));
            }

            var timer = Stopwatch.StartNew();
            while (true)
            {
                while (_messages.Count > 0)
                {
                    var msg = _messages.Dequeue();
                    if (msg.Length >= 3 && msg[0] == 0x7F && msg[2] == 0x78)
                    {
                        _log("Module busy, waiting...");
                        continue;
                    }
                    return msg;
                }
                if (timer.ElapsedMilliseconds > timeoutMs)
                    throw new Tp20Exception("Timed out waiting for the module to reply");
                KeepAlive();
            }
        }

        /// <summary>Keeps the channel alive; also gives the module a chance to deliver any pending data.</summary>
        public void KeepAlive() => Process(_elm.SendFrame(new[] { 0xA3 }));

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            try
            {
                _elm.SendFrame(new[] { 0xA8 });
            }
            catch (IOException e)
            {
                _log("Ignoring error while closing channel: " + e.Message);
            }
        }

        private void Process(List<CanFrame> frames)
        {
            int ackSeq = -1;
            foreach (var frame in frames)
            {
                var d = frame.Data;
                if (d.Length == 0) continue;
                int opcode = d[0] >> 4;
                int seq = d[0] & 0x0F;
                switch (opcode)
                {
                    case 0x0:
                    case 0x1:
                    case 0x2:
                    case 0x3:
                        if (seq == _lastRxSeq)
                        {
                            _log("Ignoring repeated frame " + seq);
                        }
                        else
                        {
                            _lastRxSeq = seq;
                            AppendData(d);
                        }
                        if (opcode == 0x0 || opcode == 0x1) ackSeq = seq;
                        break;
                    case 0x9:
                    case 0xB:
                        break; // acknowledgement of our data
                    case 0xA:
                        if (d[0] == 0xA8)
                        {
                            IsOpen = false;
                            throw new Tp20Exception("Module closed the diagnostic channel");
                        }
                        break;
                    default:
                        _log("Ignoring unknown TP2.0 frame " + frame);
                        break;
                }
            }
            if (ackSeq >= 0)
                Process(_elm.SendFrame(new[] { 0xB0 | ((ackSeq + 1) & 0x0F) }));
        }

        private void AppendData(int[] d)
        {
            if (_assembling == null)
            {
                if (d.Length < 3) return;
                _expectedLength = ((d[1] << 8) | d[2]) & 0x7FFF;
                _assembling = d.Skip(3).ToList();
            }
            else
            {
                _assembling.AddRange(d.Skip(1));
            }
            if (_assembling.Count >= _expectedLength)
            {
                _messages.Enqueue(_assembling.Take(_expectedLength).ToArray());
                _assembling = null;
            }
        }
    }
}
