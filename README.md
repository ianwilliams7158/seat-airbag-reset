# Seat Leon 1P airbag fault reset (Android)

This Android app reads and clears the stored fault codes in the airbag control unit (VAG module **15**)
of a Seat Leon Mk2 (1P, 2005–2012) through a Bluetooth ELM327 OBD2 adapter. Clearing the stored
faults turns the airbag warning light off.

It should also work on other PQ35 cars that use the VW TP2.0 CAN diagnostic protocol (Golf 5/6, A3 8P,
Octavia 1Z, Altea/Toledo 5P), but I have only built it with the Leon in mind.

## Get the APK

The GitHub Action in `.github/workflows/build-apk.yml` builds the app on every push.
To install it:

1. Open the repository's **Releases** page on your phone and download `SeatAirbagReset.apk` from
   **Seat Leon Airbag Reset (latest build)**. The same file is also attached as an artifact to each
   Actions run.
2. Open the file. Android will ask you to allow installing apps from your browser or file manager.

To build it yourself, open this folder in Android Studio, or run `./gradlew assembleDebug`.

## Use it

1. **Fix the cause first.** On the Leon 1P, the passenger airbag key switch (E224) and its wiring sit
   on the glovebox. Make sure every connector is fully clicked in and the key switch is set to **ON**.
   If the fault is still physically there, the module will not let you clear it.
2. Plug the ELM327 into the OBD port under the dashboard. Turn the ignition **ON**, but leave the engine off.
3. In Android Bluetooth settings, pair the adapter. The PIN is usually `1234` or `0000`.
4. In the app, tap **Choose adapter**, pick the ELM327, then tap **Connect**. The app should report
   the airbag module part number (for example `1P0909605...`).
5. Tap **Read faults** to see what is stored. Then tap **Clear faults**.
6. Turn the ignition OFF, wait 10 seconds, then turn it back ON. The airbag light should come on for
   the bulb check and then go out.

If **Clear faults** reports that a code is still present, the fault is still active. Check the
connectors again. If the module reports **00595 Crash Data Stored**, no scan tool can clear it: the
module itself has to be replaced or recoded.

## About ELM327 adapters

On 2005+ VAG cars, the airbag module only speaks VW's proprietary TP2.0 protocol over CAN. Generic
OBD2 apps cannot reach it. This app drives the ELM327 in raw CAN mode (`ATCAF0`, `ATSH`, `ATCRA`),
which a good-quality adapter supports. Some very cheap clones (often labelled "v2.1") leave these
commands out. With one of those, the app stops during connection and names the command the adapter
rejected. A genuine-chip or decent v1.5 clone adapter (around £10–15) works.

## How it works

- `obd/Elm327.kt`: adapter setup (500 kbit/s 11-bit CAN, raw frames) and sending/receiving frames.
- `obd/Tp20Channel.kt`: VW TP2.0 transport: opens a channel to module 0x15 via CAN ID 0x200,
  negotiates timing, splits and reassembles messages, and handles ACKs and keep-alives.
- `obd/AirbagModule.kt`: KWP2000 services: `10 89` diagnostic session, `1A 9B` identification,
  `18 02 FF 00` read DTCs, `14 FF 00` clear DTCs.
- `MainActivity.kt`: Bluetooth SPP connection and UI.

The unit tests (`app/src/test`) run the full protocol against a simulated ELM327 and airbag ECU.

The app does not write coding, adaptation, or crash data. It reads and clears fault memory, the
same operation as VCDS "Clear Codes" on module 15.

## Windows version (USB ELM327)

`windows/` contains the same tool as a Windows program, for USB ELM327 adapters (which appear as a COM port).
Download `SeatAirbagReset.exe` from the **Seat Leon Airbag Reset for Windows (latest build)** release:
https://github.com/ianwilliams7158/seat-airbag-reset/releases/download/windows-app-latest/SeatAirbagReset.exe

It runs on Windows 10/11 with nothing to install. If Windows SmartScreen warns, click **More info → Run anyway**.
Pick the adapter's COM port (Device Manager → Ports (COM & LPT) shows which one), press **Connect**, then
**Read faults** / **Clear faults**. The program finds the adapter's baud rate automatically.
