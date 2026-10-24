# Simulator tests

Runs the driver code on Linux under Mono against a fake `SonyMTPCamera.dll`, so the driver can be tested without Windows or a camera.

This is not part of the Visual Studio solution and is never built into the driver.

## What's here

| File | Purpose |
|---|---|
| `FakeSonyMTPCamera.c` | Simulated camera with the same C API as [`SonyMTPCamera.h`](https://github.com/dougforpres/SonyCamera/blob/master/SonyMTPCamera/SonyMTPCamera.h). It can be told to fail in specific ways (open, start, truncated data, liveview, ISO options). |
| `Tests.cs` | Tests for `SonyCamera` / `SonyImage`: image conversion for every personality and format, failure paths, gain, conversion timing, and a 300-capture memory check. |
| `DriverTests.cs` | Tests for the ASCOM `Camera` class: connection, `ImageArray`, `ReadoutMode`, timestamps, `MaxADU`, subframes, multiple instances, and concurrent disconnect/poll. |
| `Stubs.cs` | In-memory stand-ins for ASCOM's `TraceLogger`, `Profile` and `Util`, which need Windows and the VB runtime. Used only in the test build; the script also compiles the driver against the real ASCOM assemblies to check it builds. |
| `run.sh` | Downloads the ASCOM Platform assemblies from NuGet, builds everything and runs the tests. |

## Running

You need gcc, `mono-mcs`, `mono-runtime`, the Mono WinForms, Drawing and Configuration.Install libraries, `curl` and `unzip`. On Ubuntu:

```sh
sudo apt-get install gcc mono-mcs mono-runtime libmono-system-windows-forms4.0-cil \
    libmono-system-drawing4.0-cil libmono-system-configuration-install4.0-cil curl unzip
```

Then:

```sh
./run.sh                       # test the current source
./run.sh --baseline 7346839    # also test an older revision and diff the image output
```

`run.sh` exits non-zero if any test fails. It writes build output to `bin/` and the NuGet download to `packages/`; both are git-ignored.

`HASH` lines in `bin/*.out` fingerprint the converted image for each size, personality and output format. With `--baseline`, the script diffs them, so any change to pixel output shows up.

## Limits

- Mono's runtime, garbage collector and JIT differ from .NET Framework on Windows. Treat the timings as relative (old vs new), not as what users will see.
- The fake DLL only models the behaviour the driver relies on. It is not a replacement for testing with a real camera, ASCOM Conform, or the client apps (N.I.N.A, APT, SharpCap).
