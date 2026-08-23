# RXDK-Samples

<p align="center"><b>The original Xbox XDK sample suite, ported to build against the open RXDK SDK — with a GUI harness for building and running them on emulator or real hardware</b></p>

<p align="center">
  <a href="https://github.com/Team-Resurgent/RXDK-Samples/blob/main/LICENSE.md"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPL v3"></a>
  <a href="https://discord.gg/VcdSfajQGK"><img src="https://img.shields.io/badge/chat-on%20discord-7289da.svg?logo=discord" alt="Discord"></a>
</p>

<p align="center">
  <a href="https://ko-fi.com/J3J7L5UMN"><img src="https://ko-fi.com/img/githubbutton_sm.svg" alt="ko-fi"></a>
  <a href="https://www.patreon.com/teamresurgent"><img src="https://img.shields.io/badge/Patreon-F96854?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
</p>

The original Xbox XDK sample suite, ported to build against **RXDK** (the open,
self-contained Xbox SDK — see [RXDK-Libs](https://github.com/Team-Resurgent/RXDK-Libs)),
together with a GUI harness for building and running them on emulator or real
hardware.

## Layout

```
RxdkSamples/        The sample projects, grouped by category:
                      Certification/ Common/ DSP/ Graphics/ Input/ Misc/
                      Networking/ ReferenceUI/ Sound/ Storage/ Video/ XboxLive3/
                    Common/ holds the shared sample framework (XBApp, XBFont,
                    XBInput, XBResource, …) the individual samples build against.
RxdkSampleRunner/   Avalonia (.NET) app to batch-build the samples and launch
                    them in xemu or deploy + run them on a devkit over xbdm,
                    with a streamed console and screenshot-to-clipboard.
```

## Prerequisites

- The **RXDK SDK** installed at `C:\ProgramData\RXDK\sdk` (headers + libs).
  Install it via the RXDK VS Code / Visual Studio extension, or point the
  samples at your own SDK path.
- **Visual Studio 2022** with the RXDK `Xbox` platform (the samples are MSBuild
  `.vcxproj` projects that build with `/p:Platform=Xbox`), or use the runner.
- For `RxdkSampleRunner`: **.NET 8+**.

## Building a sample

Each sample is a standard MSBuild project. From a sample's project directory:

```powershell
msbuild <Sample>.vcxproj /p:Configuration=Debug /p:Platform=Xbox
```

This compiles the PE, converts it to an `.xbe` (imagebld), and packs an `.xbe` +
`.iso` under `out/`. Deploy the `.xbe` (or run the `.iso` in xemu) to test.

## Project manifests (`rxdk.project.json`)

Every sample commits an `rxdk.project.json` alongside its `.vcxproj`. It's a
multi-config manifest (`Debug`/`Release`) that **RXDK-VSCode** loads to build the
sample on Windows, Linux, or macOS — so the same project opens in either IDE. The
`.vcxproj` is authoritative: the manifest is *derived* from it.

Each sample also commits a `<Sample>.sln` next to its `.vcxproj`, so you can open a
single sample directly in Visual Studio 2022 (double-click the `.sln`).

If you edit a sample's `.vcxproj` (add a source, change libraries/defines, …),
regenerate the committed manifests **and solutions** and commit the result:

```powershell
pwsh scripts/Generate-Manifests.ps1
```

This runs the `RxdkGenerateManifest` MSBuild target for both configurations across
all samples, merges each pair into the committed manifest, and regenerates each
per-sample `.sln`. It needs the RXDK `Xbox` platform installed (VS command *RXDK:
Install Xbox Platform*). CI runs `scripts/Generate-Manifests.ps1 -Check` and fails
a PR whose manifests or solutions are stale.

## Using the runner

`RxdkSampleRunner` discovers the sample projects, builds them, and either:

- **launches the ISO in xemu**, or
- **deploys + runs on a devkit** over xbdm (set the console IP), streaming the
  title's debug console and offering an xbdm screenshot-to-clipboard.

```powershell
cd RxdkSampleRunner
dotnet run
```

## Notes

- Build outputs (`out/`, `bin/`, `obj/`, `.xbe`, `.iso`, …) are git-ignored;
  only sources and sample media assets are tracked.
- These are the Microsoft XDK samples adapted for RXDK; they are provided for
  developing and testing the open SDK.
