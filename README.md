# SonarLite

A free, from-scratch clone of SteelSeries GG's **Sonar** mixer for Windows: route each running
app into its own audio bus, EQ each bus independently, and (on supported hardware) control it
all from a headset's ChatMix dial — without installing SteelSeries GG.

## What it does and doesn't do

| Feature | Works with |
|---|---|
| Per-app routing into Game / Chat / Media buses | Any Windows PC — no special hardware |
| Independent EQ curve per bus | Any Windows PC — no special hardware |
| Hardware ChatMix dial | **SteelSeries Arctis Nova Pro only** (reads its base station over USB HID) |
| Automatic default-device switching on headset connect/disconnect | **Devices with "Arctis" in their Windows device name only** |

If you don't have an Arctis Nova Pro, SonarLite still works as a per-app mixer/EQ — you just
won't get the hardware dial or auto-switching.

## What you need before installing

SonarLite doesn't create virtual audio devices itself — it relies on one free driver to do that,
plus the .NET runtime to run on.

1. **Windows 10 or 11.**
2. **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)** (the "Desktop
   Runtime" x64 installer, not just the ASP.NET or generic runtime). Skip this if you already
   have Visual Studio 2022+ with .NET 8 installed.
3. **[VB-Audio VB-Cable](https://vb-audio.com/Cable/)** — download the free "VB-CABLE Driver"
   zip (**not** the paid "VB-Cable A+B" pack), run `VBCABLE_Setup_x64.exe` as administrator, and
   reboot when it asks. This installs one virtual audio device pair named `CABLE Input` /
   `CABLE Output` — SonarLite looks for that exact name, so don't rename it during setup.

That's it — no other software required. In particular, SonarLite does **not** need Equalizer
APO or SteelSeries GG installed; all EQ runs in SonarLite's own DSP chain. (If you happen to have
an old Equalizer APO install left over from an earlier SonarLite version, it's harmlessly
detected and neutralized on startup — see `Services/EqualizerApoService.cs` — but a machine
without Equalizer APO never touches that code path at all.)

## Getting started

1. Install the two dependencies above and reboot.
2. Download or build SonarLite (see [Building](#building) below) and run `SonarLite.exe`. It
   starts minimized to the system tray.
3. Open it from the tray icon. It auto-detects the VB-Cable device; assign running apps to a
   bus and adjust each bus's EQ from the window.
4. If you have an Arctis Nova Pro, plug it in — the ChatMix dial and auto-switching pick it up
   automatically, no extra configuration needed.

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
