package com.seatleon.airbagreset.obd

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AirbagModuleTest {
    private fun connect(fake: FakeElmWithAirbagEcu): AirbagModule {
        val elm = Elm327(fake.input, fake.output) { println(it) }
        elm.initialiseForRawCan()
        assertEquals("ELM327 v1.5", elm.version)
        return AirbagModule(elm) { println(it) }
    }

    @Test
    fun readsIdentification() {
        val fake = FakeElmWithAirbagEcu(mutableListOf())
        assertEquals("1P0909605D  AIRBAG VW10", connect(fake).readIdentification())
    }

    @Test
    fun readsMultiFrameFaultList() {
        val fake = FakeElmWithAirbagEcu(mutableListOf(Dtc(1280, 0x23), Dtc(589, 0x24), Dtc(532, 0x08)))
        val faults = connect(fake).readFaults()
        assertEquals(listOf(1280, 589, 532), faults.map { it.code })
        assertEquals("01280", faults[0].codeText)
        assertEquals(0x24, faults[1].status)
        // Channel opened to airbag (0x15) and the response frames were acknowledged.
        assertTrue(fake.sentFrames.contains("200:15C00010000301"))
        assertTrue(fake.sentFrames.any { it.startsWith("740:B") })
    }

    @Test
    fun clearsFaultsThroughResponsePending() {
        val fake = FakeElmWithAirbagEcu(mutableListOf(Dtc(1280, 0x23)))
        val remaining = connect(fake).clearFaults()
        assertEquals(1, fake.clearRequests)
        assertTrue(remaining.isEmpty())
    }

    @Test
    fun reportsFaultThatIsStillPresent() {
        val fake = FakeElmWithAirbagEcu(mutableListOf(Dtc(589, 0x24)), faultPresent = true, answerClearWithPending = false)
        val remaining = connect(fake).clearFaults()
        assertEquals(listOf(589), remaining.map { it.code })
    }

    @Test(expected = ElmException::class)
    fun rejectsAdapterWithoutRawCan() {
        connect(FakeElmWithAirbagEcu(mutableListOf(), clonePartial = true))
    }
}
