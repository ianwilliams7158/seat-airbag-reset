package com.seatleon.airbagreset.obd

import java.io.InputStream
import java.io.OutputStream

/**
 * Simulates an ELM327 in raw CAN mode connected to a PQ35 airbag module speaking TP2.0 + KWP2000.
 * Single-threaded: each command written produces its full reply (ending in '>') immediately.
 */
class FakeElmWithAirbagEcu(
    var faults: MutableList<Dtc>,
    private val faultPresent: Boolean = false,
    private val answerClearWithPending: Boolean = true,
    private val clonePartial: Boolean = false,
) {
    private val outBuf = StringBuilder()
    private val lineBuf = StringBuilder()
    private var header = 0x7DF
    private var filter = 0
    private var ecuSeq = 0
    private val rx = mutableListOf<Int>()
    private var rxLen = -1
    private val pendingFrames = mutableListOf<String>()
    var clearRequests = 0
    val sentFrames = mutableListOf<String>()

    val input = object : InputStream() {
        override fun available() = outBuf.length
        override fun read(): Int {
            if (outBuf.isEmpty()) return -1
            val c = outBuf[0]; outBuf.deleteCharAt(0); return c.code
        }
    }

    val output = object : OutputStream() {
        override fun write(b: Int) {
            if (b == '\r'.code) {
                handle(lineBuf.toString()); lineBuf.clear()
            } else lineBuf.append(b.toChar())
        }
    }

    private fun reply(lines: List<String>) {
        lines.forEach { outBuf.append(it).append('\r') }
        outBuf.append("\r>")
    }

    private fun handle(cmd: String) {
        if (cmd.startsWith("AT")) {
            when {
                cmd == "ATZ" -> reply(listOf("ELM327 v1.5"))
                cmd == "ATCAF0" && clonePartial -> reply(listOf("?"))
                cmd.startsWith("ATSH") -> { header = cmd.substring(4).toInt(16); reply(listOf("OK")) }
                cmd.startsWith("ATCRA") -> { filter = cmd.substring(5).toInt(16); reply(listOf("OK")) }
                else -> reply(listOf("OK"))
            }
            return
        }
        sentFrames += "%03X:%s".format(header, cmd)
        val d = cmd.chunked(2).map { it.toInt(16) }
        val out = mutableListOf<String>()
        when {
            header == 0x200 && d[0] == 0x15 && d[1] == 0xC0 -> out += "21500D00003400701"
            header == 0x740 && d[0] == 0xA0 -> out += "300A10F8AFF4AFF"
            header == 0x740 && d[0] == 0xA3 -> { out += "300A10F8AFF4AFF"; out += pendingFrames; pendingFrames.clear() }
            header == 0x740 && d[0] == 0xA8 -> Unit
            header == 0x740 && d[0] shr 4 == 0xB -> Unit
            header == 0x740 && d[0] shr 4 <= 0x3 -> {
                val seq = d[0] and 0xF
                if (rxLen < 0) { rxLen = (d[1] shl 8) or d[2]; rx += d.drop(3) } else rx += d.drop(1)
                if (d[0] shr 4 <= 1) out += "300B%X".format((seq + 1) and 0xF)
                if (rx.size >= rxLen) {
                    val req = rx.take(rxLen); rx.clear(); rxLen = -1
                    out += frames(kwp(req))
                }
            }
            else -> { reply(listOf("NO DATA")); return }
        }
        reply(out.filter { it.substring(0, 3).toInt(16) == filter }.ifEmpty { listOf("NO DATA") })
    }

    private fun kwp(req: List<Int>): List<Int> = when (req[0]) {
        0x10 -> listOf(0x50, req[1])
        0x1A -> listOf(0x5A, 0x9B) + "1P0909605D  AIRBAG VW10".map { it.code }
        0x18 -> listOf(0x58, faults.size) + faults.flatMap { listOf(it.code shr 8, it.code and 0xFF, it.status) }
        0x14 -> {
            clearRequests++
            faults = if (faultPresent) faults.take(1).toMutableList() else mutableListOf()
            if (answerClearWithPending) {
                pendingFrames += frames(listOf(0x54, 0xFF, 0x00))
                listOf(0x7F, 0x14, 0x78)
            } else listOf(0x54, 0xFF, 0x00)
        }
        else -> listOf(0x7F, req[0], 0x11)
    }

    private fun frames(payload: List<Int>): List<String> {
        val chunks = (listOf(payload.size shr 8, payload.size and 0xFF) + payload).chunked(7)
        return chunks.mapIndexed { i, c ->
            val op = if (i == chunks.lastIndex) 0x10 else 0x20
            val b0 = op or ecuSeq
            ecuSeq = (ecuSeq + 1) and 0xF
            "300" + (listOf(b0) + c).joinToString("") { "%02X".format(it) }
        }
    }
}
