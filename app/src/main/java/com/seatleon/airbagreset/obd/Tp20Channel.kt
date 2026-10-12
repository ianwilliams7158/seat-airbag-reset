package com.seatleon.airbagreset.obd

import java.io.IOException

class Tp20Exception(message: String) : IOException(message)

/**
 * VW "Transport Protocol 2.0" over CAN, as used by PQ35 vehicles (Seat Leon 1P, Golf 5/6, A3 8P...)
 * for KWP2000 diagnostics. Implemented on top of an ELM327 in raw CAN mode.
 *
 * Data frame byte 0: high nibble = opcode, low nibble = sequence number.
 *   0x0 / 0x1 = more follow / last frame, receiver must ACK
 *   0x2 / 0x3 = more follow / last frame, no ACK expected
 *   0xB / 0x9 = ACK (ready / not ready) carrying the next expected sequence number
 *   0xA0..0xA8 = channel parameters, keep-alive, disconnect
 */
class Tp20Channel(
    private val elm: Elm327,
    private val log: (String) -> Unit = {},
) {
    private var txSeq = 0
    private var lastRxSeq = -1
    private var assembling: MutableList<Int>? = null
    private var expectedLength = 0
    private val messages = ArrayDeque<IntArray>()

    var isOpen = false
        private set

    fun open(moduleAddress: Int) {
        elm.setHeader(0x200)
        elm.setReceiveFilter(0x200 + moduleAddress)
        // Ask the module to open a channel: we will listen on 0x300, the module picks its own ID.
        val replies = elm.sendFrame(intArrayOf(moduleAddress, 0xC0, 0x00, 0x10, 0x00, 0x03, 0x01))
        val setup = replies.firstOrNull { it.data.size >= 7 && it.data[1] == 0xD0 }
        if (setup == null) {
            val refusal = replies.firstOrNull { it.data.size >= 2 && it.data[1] in 0xD6..0xD8 }
            if (refusal != null) {
                throw Tp20Exception("Module 0x%02X refused the diagnostic channel (code %02X)".format(moduleAddress, refusal.data[1]))
            }
            throw Tp20Exception("No answer from module 0x%02X. Check the ignition is ON and the adapter is plugged in.".format(moduleAddress))
        }
        val rxId = setup.data[2] or ((setup.data[3] and 0x07) shl 8)
        val txId = setup.data[4] or ((setup.data[5] and 0x07) shl 8)
        log("Channel open: tester sends on %03X, module replies on %03X".format(txId, rxId))

        elm.setHeader(txId)
        elm.setReceiveFilter(rxId)
        val params = elm.sendFrame(intArrayOf(0xA0, 0x0F, 0x8A, 0xFF, 0x32, 0xFF))
        if (params.none { it.data.firstOrNull() == 0xA1 }) {
            throw Tp20Exception("Module did not accept channel timing parameters")
        }
        txSeq = 0
        lastRxSeq = -1
        assembling = null
        messages.clear()
        isOpen = true
    }

    /** Sends a KWP2000 request and returns the final reply (skipping "response pending" replies). */
    fun request(payload: IntArray, timeoutMs: Long = 8000): IntArray {
        check(isOpen) { "Channel not open" }
        messages.clear()
        val chunks = (intArrayOf(payload.size shr 8 and 0xFF, payload.size and 0xFF) + payload).toList().chunked(7)
        for ((i, chunk) in chunks.withIndex()) {
            val opcode = if (i == chunks.lastIndex) 0x10 else 0x20
            val frame = intArrayOf(opcode or txSeq) + chunk.toIntArray()
            txSeq = (txSeq + 1) and 0x0F
            process(elm.sendFrame(frame))
        }

        val deadline = System.currentTimeMillis() + timeoutMs
        while (true) {
            while (messages.isNotEmpty()) {
                val msg = messages.removeFirst()
                if (msg.size >= 3 && msg[0] == 0x7F && msg[2] == 0x78) {
                    log("Module busy, waiting...")
                    continue
                }
                return msg
            }
            if (System.currentTimeMillis() > deadline) {
                throw Tp20Exception("Timed out waiting for the module to reply")
            }
            keepAlive()
        }
    }

    /** Keeps the channel alive; also gives the module a chance to deliver any pending data. */
    fun keepAlive() = process(elm.sendFrame(intArrayOf(0xA3)))

    fun close() {
        if (!isOpen) return
        isOpen = false
        try {
            elm.sendFrame(intArrayOf(0xA8))
        } catch (e: IOException) {
            log("Ignoring error while closing channel: ${e.message}")
        }
    }

    private fun process(frames: List<CanFrame>) {
        var ackSeq = -1
        for (frame in frames) {
            val d = frame.data
            if (d.isEmpty()) continue
            val opcode = d[0] shr 4
            val seq = d[0] and 0x0F
            when (opcode) {
                0x0, 0x1, 0x2, 0x3 -> {
                    if (seq == lastRxSeq) {
                        log("Ignoring repeated frame $seq")
                    } else {
                        lastRxSeq = seq
                        appendData(d)
                    }
                    if (opcode == 0x0 || opcode == 0x1) ackSeq = seq
                }
                0x9, 0xB -> Unit // acknowledgement of our data
                0xA -> if (d[0] == 0xA8) {
                    isOpen = false
                    throw Tp20Exception("Module closed the diagnostic channel")
                }
                else -> log("Ignoring unknown TP2.0 frame $frame")
            }
        }
        if (ackSeq >= 0) {
            process(elm.sendFrame(intArrayOf(0xB0 or ((ackSeq + 1) and 0x0F))))
        }
    }

    private fun appendData(d: IntArray) {
        val current = assembling
        if (current == null) {
            if (d.size < 3) return
            expectedLength = ((d[1] shl 8) or d[2]) and 0x7FFF
            assembling = d.drop(3).toMutableList()
        } else {
            current += d.drop(1)
        }
        val buf = assembling!!
        if (buf.size >= expectedLength) {
            messages.addLast(buf.take(expectedLength).toIntArray())
            assembling = null
        }
    }
}
