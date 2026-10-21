# Dictum

Voice to text for Windows and Linux: hold a hotkey, speak, and the text lands in the app you are typing in. Speech recognition runs on your machine with Whisper (on the GPU through Vulkan when it can); nothing is sent anywhere unless you add a cloud provider yourself.

Dictum has two programs, installed together:

- `dictum-gui`: the tray app with the setup window, the settings and the on-screen overlay.
- `dictum`: the command line and the background daemon that listens for the hotkey.

## Documentation

The guides, the reference and the troubleshooting are at [docs.sigil.build/dictum](https://docs.sigil.build/dictum/). In Ukrainian: [docs.sigil.build/uk/dictum](https://docs.sigil.build/uk/dictum/).

## Install

Download the package for your system from [GitHub Releases](https://github.com/Sigil-build/dictum/releases): an MSI or a zip for Windows 11, an AppImage or a tarball for Linux (x86-64). For Arch and CachyOS, use the `PKGBUILD` attached to each release, which carries the tarball's checksum.

The steps for each package, the requirements and the first run are in [Getting started](https://docs.sigil.build/dictum/getting-started/).

## Building from source

You need the .NET 10 SDK; for native binaries, also the platform's C toolchain (MSVC build tools on Windows, `clang` and `zlib1g-dev` on Linux).

```sh
dotnet build
dotnet test
dotnet publish src/Dictum.Cli -c Release -r linux-x64     # or win-x64; native AOT builds only for the OS it runs on
packaging/package.sh linux-x64 artifacts/packages        # the packages for this OS (the MSI needs Windows)
```

`packaging/tests/test-package.sh` builds and checks the packages for the current OS.

## License

Dictum is released under the MIT License: see [LICENSE](LICENSE). The third-party components it ships (the .NET runtime, Avalonia, whisper.cpp, PortAudio, libuiohook and others) keep their own licenses; [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) lists them with the license texts. Every package carries both files. Speech models are downloaded separately, each under its own license, which the setup shows.
