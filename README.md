# SonarLite

A free, from-scratch clone of SteelSeries GG's **Sonar** mixer, built for the Arctis Nova Pro
(and similar headsets) without SteelSeries GG's bloat or telemetry.

## Features

- **Per-app routing** into independent Game / Chat / Media buses
- **Per-bus EQ** with its own curve for each bus
- **Hardware ChatMix dial** support (reads the headset's HID ChatMix wheel directly)
- **Tray-resident**, low-memory, snappy — native Win32 tray icon, no WinForms
- Automatic default-device switching when the headset connects/disconnects

## Requirements

- Windows 10/11
- .NET 8 Desktop Runtime
- [VB-Audio VB-Cable](https://vb-audio.com/Cable/) — the **free** single virtual cable (not the
  paid VB-Cable A+B pack). SonarLite achieves three independent EQ buses from that one cable via
  per-PID process loopback capture plus a silent sink.

## Building

```
dotnet build SonarLite.csproj -c Release
```

Or open in Visual Studio 2022+ and build normally. Output is a self-contained tray application;
no installer is provided yet — copy the build output and run `SonarLite.exe`.

## Status

Actively developed, single-maintainer hobby project. Expect rough edges.

## License

MIT — see [LICENSE](LICENSE).
