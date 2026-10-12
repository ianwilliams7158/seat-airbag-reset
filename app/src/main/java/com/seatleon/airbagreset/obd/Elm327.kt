package com.seatleon.airbagreset.obd

import java.io.IOException
import java.io.InputStream
import java.io.OutputStream

class ElmException(message: String) : IOException(message)

/** A raw CAN frame as printed by the ELM327 with headers on (ATH1) and auto-formatting off (ATCAF0). */
class CanFrame(val id: Int, val data: IntArray) {
    override fun toString(): String =
        "%03X ".format(id) + data.joinToString(" ") { "%02X".format(it) }
}

/**
 * Minimal ELM327 driver. Works over any byte stream (Bluetooth RFCOMM socket on Android,
 * or an in-memory simulator in the unit tests).
 */
class Elm327(
    private val input: InputStream,
    private val output: OutputStream,
    private val log: (String) -> Unit = {},
) {
    var version: String = "unknown"
        private set

    /** Sends a command and returns the non-empty reply lines received before the '>' prompt. */
    fun command(cmd: String, timeoutMs: Long = 3000): List<String> {
        drain()
        log("> $cmd")
        output.write("$cmd\r".toByteArray(Charsets.US_ASCII))
        output.flush()

        val reply = StringBuilder()
        val buf = ByteArray(256)
        val deadline = System.currentTimeMillis() + timeoutMs
        while (true) {
            val available = input.available()
            if (available > 0) {
                val n = input.read(buf, 0, minOf(available, buf.size))
                if (n < 0) throw ElmException("Adapter connection closed")
                val text = String(buf, 0, n, Charsets.US_ASCII)
                val prompt = text.indexOf('>')
                if (prompt >= 0) {
                    reply.append(text, 0, prompt)
                    break
                }
                reply.append(text)
            } else {
                if (System.currentTimeMillis() > deadline) {
                    throw ElmException("Timed out waiting for the adapter to answer '$cmd'")
                }
                Thread.sleep(2)
            }
        }

        val lines = reply.toString()
            .split('\r', '\n')
            .map { line -> line.filter { it >= ' ' }.trim() }
            .filter { it.isNotEmpty() && it != cmd && !it.startsWith("SEARCHING") }
        lines.forEach { log("< $it") }
        return lines
    }

    private fun drain() {
        while (input.available() > 0) input.read()
    }

    private fun expectOk(cmd: String, required: Boolean = true) {
        val reply = command(cmd)
        if (reply.none { it.contains("OK") }) {
            val msg = "Adapter rejected $cmd (reply: ${reply.joinToString(" ").ifEmpty { "nothing" }})"
            if (required) {
                throw ElmException("$msg. This ELM327 clone does not appear to support raw CAN mode, which is needed to talk to VAG modules.")
            }
            log("Warning: $msg - continuing")
        }
    }

    /**
     * Resets the adapter and configures it to send/receive raw 11-bit CAN frames at 500 kbit/s,
     * which is what VW-group TP2.0 diagnostics needs.
     */
    fun initialiseForRawCan() {
        val reset = command("ATZ", timeoutMs = 6000)
        version = reset.firstOrNull { it.contains("ELM", ignoreCase = true) } ?: "unknown"
        expectOk("ATE0")             // echo off
        expectOk("ATL0", false)      // no linefeeds
        expectOk("ATS0", false)      // no spaces
        expectOk("ATH1")             // show CAN IDs
        expectOk("ATSP6")            // ISO 15765-4 CAN, 11 bit, 500 kbit/s
        expectOk("ATAT0", false)     // fixed (not adaptive) timeout
        expectOk("ATST19", false)    // reply timeout 25 x 4 ms = 100 ms
        expectOk("ATCAF0")           // CAN auto-formatting off: we build TP2.0 frames ourselves
        expectOk("ATR1", false)      // wait for responses
    }

    fun setHeader(id: Int) = expectOk("ATSH%03X".format(id))

    fun setReceiveFilter(id: Int) = expectOk("ATCRA%03X".format(id))

    /** Transmits one CAN frame and returns every frame received before the adapter timed out. */
    fun sendFrame(data: IntArray): List<CanFrame> {
        val hex = data.joinToString("") { "%02X".format(it) }
        val frames = mutableListOf<CanFrame>()
        for (line in command(hex)) {
            val compact = line.replace(" ", "")
            when {
                compact == "NODATA" || compact == "OK" -> Unit
                compact.length >= 3 && compact.length % 2 == 1 && compact.all { it.isHexDigit() } -> {
                    val id = compact.substring(0, 3).toInt(16)
                    val bytes = compact.substring(3).chunked(2).map { it.toInt(16) }.toIntArray()
                    frames += CanFrame(id, bytes)
                }
                compact == "?" -> throw ElmException("Adapter did not understand frame $hex")
                compact.contains("ERROR") || compact.contains("UNABLETOCONNECT") ->
                    throw ElmException("CAN bus problem: $line. Is the ignition on?")
                compact.contains("BUFFERFULL") -> throw ElmException("Adapter buffer overflow")
                else -> log("Ignoring unexpected adapter output: $line")
            }
        }
        return frames
    }

    private fun Char.isHexDigit() = this in '0'..'9' || this in 'A'..'F' || this in 'a'..'f'
}
