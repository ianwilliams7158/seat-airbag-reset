package com.seatleon.airbagreset.obd

import java.io.IOException

class Dtc(val code: Int, val status: Int) {
    val codeText: String get() = "%05d".format(code)
    val description: String get() = DESCRIPTIONS[code] ?: "Unknown code - search \"VAG $codeText\""

    override fun toString() = "$codeText  $description  (status %02X)".format(status)

    companion object {
        private val DESCRIPTIONS = mapOf(
            532 to "Supply Voltage B+",
            588 to "Airbag Igniter; Driver Side (N95)",
            589 to "Airbag Igniter; Passenger Side (N131)",
            595 to "Crash Data Stored - module must be replaced/reset, cannot be cleared",
            1217 to "Side Airbag Igniter; Driver Side (N199)",
            1218 to "Side Airbag Igniter; Passenger Side (N200)",
            1221 to "Side Airbag Crash Sensor; Driver Side (G179)",
            1222 to "Side Airbag Crash Sensor; Passenger Side (G180)",
            1280 to "Airbag Deactivated; Passenger Side (check glovebox key switch E224)",
            1299 to "Diagnostic Interface for Data Bus (J533)",
        )
    }
}

class ModuleException(message: String) : IOException(message)

/** KWP2000 access to the airbag control unit (VAG address 15). */
class AirbagModule(
    private val elm: Elm327,
    private val log: (String) -> Unit = {},
) {
    private fun <T> withChannel(block: (Tp20Channel) -> T): T {
        val channel = Tp20Channel(elm, log)
        channel.open(ADDRESS)
        try {
            val session = channel.request(intArrayOf(0x10, 0x89))
            if (session.firstOrNull() != 0x50) log("Diagnostic session not accepted (${hex(session)}), continuing anyway")
            return block(channel)
        } finally {
            channel.close()
        }
    }

    /** Returns the module part number / name, e.g. "1P0909605D  AIRBAG VW10", or null if unavailable. */
    fun readIdentification(): String? = withChannel { channel ->
        val reply = channel.request(intArrayOf(0x1A, 0x9B))
        if (reply.firstOrNull() != 0x5A) null
        else reply.drop(2).filter { it in 0x20..0x7E }.map { it.toChar() }.joinToString("").trim().ifEmpty { null }
    }

    fun readFaults(): List<Dtc> = withChannel { readFaults(it) }

    /** Clears all stored faults and returns whatever is still stored afterwards. */
    fun clearFaults(): List<Dtc> = withChannel { channel ->
        val reply = channel.request(intArrayOf(0x14, 0xFF, 0x00))
        if (reply.firstOrNull() != 0x54) throw ModuleException("Module refused to clear faults: ${negative(reply)}")
        log("Clear command accepted")
        readFaults(channel)
    }

    private fun readFaults(channel: Tp20Channel): List<Dtc> {
        val reply = channel.request(intArrayOf(0x18, 0x02, 0xFF, 0x00))
        if (reply.firstOrNull() != 0x58) throw ModuleException("Module refused to report faults: ${negative(reply)}")
        return reply.drop(2).chunked(3)
            .filter { it.size == 3 }
            .map { Dtc((it[0] shl 8) or it[1], it[2]) }
            .filter { it.code != 0xFFFF }
    }

    private fun negative(reply: IntArray): String =
        if (reply.size >= 3 && reply[0] == 0x7F) "negative response code %02X".format(reply[2]) else hex(reply)

    private fun hex(bytes: IntArray) = bytes.joinToString(" ") { "%02X".format(it) }

    companion object {
        const val ADDRESS = 0x15
    }
}
