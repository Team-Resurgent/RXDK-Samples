# RXDK-Samples

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
