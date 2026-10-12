package com.seatleon.airbagreset

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.Button
import android.widget.ScrollView
import android.widget.TextView
import com.seatleon.airbagreset.obd.AirbagModule
import com.seatleon.airbagreset.obd.Dtc
import com.seatleon.airbagreset.obd.Elm327
import java.io.IOException
import java.util.UUID
import java.util.concurrent.Executors

@SuppressLint("MissingPermission") // checked in hasBluetoothPermission() before any Bluetooth call
class MainActivity : Activity() {
    private val worker = Executors.newSingleThreadExecutor()
    private val ui = Handler(Looper.getMainLooper())

    private lateinit var statusText: TextView
    private lateinit var resultText: TextView
    private lateinit var logText: TextView
    private lateinit var logScroll: ScrollView
    private lateinit var adapterButton: Button
    private lateinit var connectButton: Button
    private lateinit var readButton: Button
    private lateinit var clearButton: Button

    @Volatile private var socket: BluetoothSocket? = null
    @Volatile private var module: AirbagModule? = null
    private var busy = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        statusText = findViewById(R.id.status) as TextView
        resultText = findViewById(R.id.result) as TextView
        logText = findViewById(R.id.log) as TextView
        logScroll = findViewById(R.id.logScroll) as ScrollView
        adapterButton = findViewById(R.id.chooseAdapter) as Button
        connectButton = findViewById(R.id.connect) as Button
        readButton = findViewById(R.id.readFaults) as Button
        clearButton = findViewById(R.id.clearFaults) as Button

        adapterButton.setOnClickListener { chooseAdapter() }
        connectButton.setOnClickListener { if (socket == null) connect() else disconnect() }
        readButton.setOnClickListener { readFaults() }
        clearButton.setOnClickListener { confirmClear() }

        if (!hasBluetoothPermission() && Build.VERSION.SDK_INT >= ANDROID_12) {
            requestPermissions(arrayOf(BLUETOOTH_CONNECT), REQUEST_BT)
        }
        refreshUi()
    }

    override fun onDestroy() {
        disconnect()
        worker.shutdown()
        super.onDestroy()
    }

    private fun hasBluetoothPermission() =
        Build.VERSION.SDK_INT < ANDROID_12 ||
            checkSelfPermission(BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

    private val prefs get() = getSharedPreferences("settings", MODE_PRIVATE)

    private val bluetoothAdapter get() = getSystemService(BluetoothManager::class.java)?.adapter

    private fun chooseAdapter() {
        if (!hasBluetoothPermission()) {
            if (Build.VERSION.SDK_INT >= ANDROID_12) {
                requestPermissions(arrayOf(BLUETOOTH_CONNECT), REQUEST_BT)
            }
            return
        }
        val bt = bluetoothAdapter
        if (bt == null || !bt.isEnabled) {
            showMessage("Bluetooth is off", "Turn on Bluetooth and pair your ELM327 adapter in Android settings (PIN is usually 1234 or 0000).")
            return
        }
        val devices = bt.bondedDevices.sortedBy { it.name ?: "" }
        if (devices.isEmpty()) {
            showMessage("No paired adapters", "Pair your ELM327 in Android Bluetooth settings first (PIN is usually 1234 or 0000), then come back.")
            return
        }
        val labels = devices.map { "${it.name ?: "Unnamed"}\n${it.address}" }.toTypedArray()
        AlertDialog.Builder(this)
            .setTitle("Choose your ELM327 adapter")
            .setItems(labels) { _, which ->
                prefs.edit()
                    .putString(KEY_ADDRESS, devices[which].address)
                    .putString(KEY_NAME, devices[which].name ?: devices[which].address)
                    .apply()
                refreshUi()
            }
            .show()
    }

    private fun connect() {
        val address = prefs.getString(KEY_ADDRESS, null) ?: return chooseAdapter()
        if (!hasBluetoothPermission()) return chooseAdapter()
        val device = bluetoothAdapter?.getRemoteDevice(address) ?: return
        runTask("Connecting to adapter...") {
            val s = openSocket(device)
            socket = s
            try {
                val elm = Elm327(s.inputStream, s.outputStream, ::log)
                elm.initialiseForRawCan()
                log("Adapter: ${elm.version}")
                val m = AirbagModule(elm, ::log)
                val id = m.readIdentification()
                module = m
                "Connected to airbag module" + (id?.let { "\n$it" } ?: "")
            } catch (e: Exception) {
                disconnectQuietly()
                throw e
            }
        }
    }

    private fun openSocket(device: BluetoothDevice): BluetoothSocket {
        // We never start a scan, so this is only a precaution. Android 12+ requires BLUETOOTH_SCAN
        // for it, which we don't ask for, so skip it if not allowed.
        try {
            bluetoothAdapter?.cancelDiscovery()
        } catch (_: SecurityException) {
        }
        try {
            return device.createRfcommSocketToServiceRecord(SPP_UUID).also { it.connect() }
        } catch (e: IOException) {
            log("Standard connect failed (${e.message}), trying fallback channel 1")
            // Many cheap ELM327 clones only work on a hard-coded RFCOMM channel.
            val method = device.javaClass.getMethod("createRfcommSocket", Int::class.javaPrimitiveType)
            return (method.invoke(device, 1) as BluetoothSocket).also { it.connect() }
        }
    }

    private fun disconnect() {
        try {
            socket?.close()
        } catch (_: IOException) {
        }
        socket = null
        module = null
        if (::statusText.isInitialized) refreshUi()
    }

    private fun readFaults() {
        val m = module ?: return
        runTask("Reading airbag faults...") { describe(m.readFaults(), "No faults stored. Airbag light should be off.") }
    }

    private fun confirmClear() {
        AlertDialog.Builder(this)
            .setTitle("Clear airbag faults?")
            .setMessage(
                "Before clearing:\n\n" +
                    "• Glovebox connectors (including the passenger airbag key switch) are plugged back in\n" +
                    "• Passenger airbag key switch is set to ON\n" +
                    "• Ignition ON, engine OFF\n\n" +
                    "If a fault is still physically present it will come straight back."
            )
            .setPositiveButton("Clear faults") { _, _ -> clearFaults() }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun clearFaults() {
        val m = module ?: return
        runTask("Clearing airbag faults...") {
            val remaining = m.clearFaults()
            if (remaining.isEmpty()) {
                "Faults cleared.\n\nTurn the ignition OFF, wait 10 seconds, then back ON. " +
                    "The airbag light should come on for a few seconds during the bulb check and then go out."
            } else {
                describe(remaining, "") + "\n\nThese faults are still present, so the module refuses to forget them. " +
                    "Check the connectors behind the glovebox are fully clicked in, then try again."
            }
        }
    }

    private fun describe(faults: List<Dtc>, none: String): String =
        if (faults.isEmpty()) none
        else "${faults.size} fault(s) stored:\n\n" + faults.joinToString("\n\n") {
            "${it.codeText}\n${it.description}\nstatus %02X".format(it.status)
        }

    private fun runTask(title: String, task: () -> String) {
        if (busy) return
        busy = true
        statusText.text = title
        resultText.text = ""
        refreshUi()
        worker.execute {
            val result = try {
                task()
            } catch (e: Exception) {
                log("Error: ${e.javaClass.simpleName}: ${e.message}")
                if (socket?.isConnected != true) disconnectQuietly()
                "Failed: ${e.message}"
            }
            ui.post {
                busy = false
                resultText.text = result
                refreshUi()
            }
        }
    }

    private fun disconnectQuietly() {
        try {
            socket?.close()
        } catch (_: IOException) {
        }
        socket = null
        module = null
    }

    private fun refreshUi() {
        val name = prefs.getString(KEY_NAME, null)
        adapterButton.text = if (name == null) "Choose adapter" else "Adapter: $name"
        val connected = module != null
        connectButton.text = if (connected) "Disconnect" else "Connect"
        connectButton.isEnabled = !busy && name != null
        adapterButton.isEnabled = !busy && !connected
        readButton.isEnabled = !busy && connected
        clearButton.isEnabled = !busy && connected
        if (!busy) statusText.text = if (connected) "Connected" else "Not connected"
    }

    private fun log(line: String) {
        ui.post {
            logText.append(line + "\n")
            logScroll.post { logScroll.fullScroll(View.FOCUS_DOWN) }
        }
    }

    private fun showMessage(title: String, message: String) {
        AlertDialog.Builder(this).setTitle(title).setMessage(message).setPositiveButton("OK", null).show()
    }

    companion object {
        private const val REQUEST_BT = 1
        private const val ANDROID_12 = 31
        private const val BLUETOOTH_CONNECT = "android.permission.BLUETOOTH_CONNECT"
        private const val KEY_ADDRESS = "adapter_address"
        private const val KEY_NAME = "adapter_name"
        private val SPP_UUID: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    }
}
