using System;
using System.IO.Ports;
using System.Threading;
using AirbagReset.Obd;

namespace AirbagReset
{
    /// <summary>ELM327 on a COM port (USB adapters appear as a virtual COM port).</summary>
    public sealed class SerialPortChannel : IByteChannel, IDisposable
    {
        // USB ELM327s ship at 38400 (most common) or 115200; a few use 9600 or 57600.
        private static readonly int[] BaudRates = { 38400, 115200, 9600, 57600, 230400, 500000 };

        private readonly SerialPort _port;

        private SerialPortChannel(string portName, int baud)
        {
            _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = 500,
                WriteTimeout = 2000,
            };
            _port.Open();
            Thread.Sleep(100);
            _port.DiscardInBuffer();
        }

        public int BaudRate => _port.BaudRate;

        public int BytesAvailable => _port.BytesToRead;

        public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);

        public void Write(byte[] data) => _port.Write(data, 0, data.Length);

        public void Dispose()
        {
            try
            {
                _port.Close();
            }
            catch (Exception)
            {
            }
            _port.Dispose();
        }

        /// <summary>Opens the port at whichever speed the adapter answers on.</summary>
        public static SerialPortChannel OpenElm(string portName, Action<string> log)
        {
            foreach (int baud in BaudRates)
            {
                log("Trying " + portName + " at " + baud + " baud...");
                SerialPortChannel channel;
                try
                {
                    channel = new SerialPortChannel(portName, baud);
                }
                catch (Exception e)
                {
                    throw new ElmException("Could not open " + portName + ": " + e.Message +
                                           ". Close any other OBD program that might be using it.");
                }
                if (new Elm327(channel, log).Probe())
                {
                    log("Adapter found at " + baud + " baud");
                    return channel;
                }
                channel.Dispose();
            }
            throw new ElmException("No ELM327 answered on " + portName +
                                   ". Check you picked the right COM port (see Device Manager > Ports) and the adapter is powered (plugged into the car).");
        }
    }
}
