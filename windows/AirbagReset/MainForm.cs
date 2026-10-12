using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AirbagReset.Obd;

namespace AirbagReset
{
    public class MainForm : Form
    {
        private readonly ComboBox _portBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        private readonly Button _refreshButton = new Button { Text = "Refresh", AutoSize = true };
        private readonly Button _connectButton = new Button { Text = "Connect", AutoSize = true };
        private readonly Button _readButton = new Button { Text = "Read faults", AutoSize = true };
        private readonly Button _clearButton = new Button { Text = "Clear faults", AutoSize = true };
        private readonly Label _statusLabel = new Label { Text = "Not connected", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly TextBox _resultBox = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11f), ScrollBars = ScrollBars.Vertical };
        private readonly TextBox _logBox = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), ScrollBars = ScrollBars.Vertical };

        private SerialPortChannel _channel;
        private AirbagModule _module;
        private bool _busy;

        public MainForm()
        {
            Text = "Seat Leon 1P Airbag Reset (module 15)";
            Width = 760;
            Height = 680;
            StartPosition = FormStartPosition.CenterScreen;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
            top.Controls.Add(new Label { Text = "Adapter port:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            top.Controls.AddRange(new Control[] { _portBox, _refreshButton, _connectButton, _readButton, _clearButton, _statusLabel });

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
            split.Panel1.Controls.Add(_resultBox);
            split.Panel1.Padding = new Padding(8, 0, 8, 4);
            var logLabel = new Label { Text = "Communication log", Dock = DockStyle.Top, Font = new Font(Font, FontStyle.Bold) };
            split.Panel2.Controls.Add(_logBox);
            split.Panel2.Controls.Add(logLabel);
            split.Panel2.Padding = new Padding(8, 0, 8, 8);

            Controls.Add(split);
            Controls.Add(top);

            _refreshButton.Click += (s, e) => RefreshPorts();
            _connectButton.Click += (s, e) => { if (_module == null) Connect(); else Disconnect(); };
            _readButton.Click += (s, e) => ReadFaults();
            _clearButton.Click += (s, e) => ConfirmClear();
            FormClosed += (s, e) => Disconnect();

            _resultBox.Text =
                "1. Plug the USB ELM327 into the laptop and the car's OBD port.\r\n" +
                "2. Ignition ON, engine OFF.\r\n" +
                "3. Pick the adapter's COM port and press Connect.\r\n\r\n" +
                "Not sure which COM port? Open Device Manager > Ports (COM & LPT) and unplug/replug the adapter to see which one appears.";

            RefreshPorts();
            UpdateUi();
        }

        private void RefreshPorts()
        {
            var ports = SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p).ToArray();
            string previous = _portBox.SelectedItem as string;
            _portBox.Items.Clear();
            _portBox.Items.AddRange(ports);
            if (ports.Length == 0)
            {
                Log("No COM ports found. Plug in the adapter (and install its USB driver if Windows asks), then press Refresh.");
            }
            else
            {
                // Default to the most recently added port, which is usually the USB adapter.
                _portBox.SelectedItem = previous != null && ports.Contains(previous) ? previous : ports.Last();
            }
            UpdateUi();
        }

        private void Connect()
        {
            string port = _portBox.SelectedItem as string;
            if (port == null) return;
            RunTask("Connecting...", () =>
            {
                var channel = SerialPortChannel.OpenElm(port, Log);
                _channel = channel;
                try
                {
                    var elm = new Elm327(channel, Log);
                    elm.InitialiseForRawCan();
                    Log("Adapter: " + elm.Version);
                    var module = new AirbagModule(elm, Log);
                    string id = module.ReadIdentification();
                    _module = module;
                    return "Connected to airbag module" + (id != null ? "\r\n" + id : "");
                }
                catch (Exception)
                {
                    CloseChannel();
                    throw;
                }
            });
        }

        private void Disconnect()
        {
            CloseChannel();
            UpdateUi();
        }

        private void CloseChannel()
        {
            _module = null;
            _channel?.Dispose();
            _channel = null;
        }

        private void ReadFaults()
        {
            var module = _module;
            if (module == null) return;
            RunTask("Reading airbag faults...", () => Describe(module.ReadFaults(), "No faults stored. The airbag light should be off."));
        }

        private void ConfirmClear()
        {
            var answer = MessageBox.Show(this,
                "Before clearing:\r\n\r\n" +
                "• Glovebox connectors (including the passenger airbag key switch) are plugged back in\r\n" +
                "• Passenger airbag key switch is set to ON\r\n" +
                "• Ignition ON, engine OFF\r\n\r\n" +
                "If a fault is still physically present it will come straight back.\r\n\r\nClear airbag faults now?",
                "Clear airbag faults?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.OK) ClearFaults();
        }

        private void ClearFaults()
        {
            var module = _module;
            if (module == null) return;
            RunTask("Clearing airbag faults...", () =>
            {
                var remaining = module.ClearFaults();
                if (remaining.Count == 0)
                {
                    return "Faults cleared.\r\n\r\nTurn the ignition OFF, wait 10 seconds, then back ON. " +
                           "The airbag light should come on for a few seconds during the bulb check and then go out.";
                }
                return Describe(remaining, "") + "\r\n\r\nThese faults are still present, so the module refuses to forget them. " +
                       "Check the connectors behind the glovebox are fully clicked in, then try again.";
            });
        }

        private static string Describe(List<Dtc> faults, string none)
        {
            if (faults.Count == 0) return none;
            return faults.Count + " fault(s) stored:\r\n\r\n" + string.Join("\r\n\r\n",
                faults.Select(f => f.CodeText + "\r\n" + f.Description + "\r\nstatus " + f.Status.ToString("X2")));
        }

        private void RunTask(string title, Func<string> task)
        {
            if (_busy) return;
            _busy = true;
            _statusLabel.Text = title;
            _resultBox.Text = "";
            UpdateUi();
            Task.Run(() =>
            {
                string result;
                try
                {
                    result = task();
                }
                catch (Exception e)
                {
                    Log("Error: " + e.GetType().Name + ": " + e.Message);
                    result = "Failed: " + e.Message;
                }
                BeginInvoke((Action)(() =>
                {
                    _busy = false;
                    _resultBox.Text = result;
                    UpdateUi();
                }));
            });
        }

        private void UpdateUi()
        {
            bool connected = _module != null;
            _connectButton.Text = connected ? "Disconnect" : "Connect";
            _connectButton.Enabled = !_busy && (connected || _portBox.SelectedItem != null);
            _portBox.Enabled = !_busy && !connected;
            _refreshButton.Enabled = !_busy && !connected;
            _readButton.Enabled = !_busy && connected;
            _clearButton.Enabled = !_busy && connected;
            if (!_busy) _statusLabel.Text = connected ? "Connected" : "Not connected";
        }

        private void Log(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => Log(line)));
                return;
            }
            _logBox.AppendText(line + "\r\n");
        }
    }
}
