# Dictum

[![CI](https://github.com/Sigil-build/dictum/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Sigil-build/dictum/actions/workflows/ci.yml?query=branch%3Amain)
[![Latest release](https://img.shields.io/github/v/release/Sigil-build/dictum)](https://github.com/Sigil-build/dictum/releases/latest)
[![License: MIT](https://img.shields.io/github/license/Sigil-build/dictum)](LICENSE)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/Sigil-build/dictum/badge)](https://scorecard.dev/viewer/?uri=github.com/Sigil-build/dictum)
[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=Sigil-build_dictum&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=Sigil-build_dictum)

Voice to text for Windows and Linux: hold a hotkey, speak, and the text lands in the app you are typing in. Speech recognition runs on your machine with Whisper (on the GPU through Vulkan when it can); your speech and text are not sent anywhere unless you add a cloud provider yourself.

![Dictum's Settings window: a profile with its hotkey, two languages and its own words](docs/images/settings-profiles.png#gh-light-mode-only)
![Dictum's Settings window: a profile with its hotkey, two languages and its own words](docs/images/settings-profiles-dark.png#gh-dark-mode-only)

## What it does

- **Dictate into any app.** Hold the hotkey (`Ctrl+Alt+Space` by default), speak and let go: Dictum pastes the text where your cursor is, then puts back what your clipboard held. [Getting started](https://docs.sigil.build/dictum/getting-started/) takes you from the download to your first dictation.
- **Speech stays on your computer.** Whisper runs locally, and there is no telemetry. [Privacy](https://docs.sigil.build/dictum/privacy/) lists what Dictum uses (the microphone, a keyboard hook, the clipboard, the keyring) and when.
- **Several languages.** The main language has priority: Dictum takes another one only when Whisper is sure of it, so your words are not translated by mistake. See [Languages](https://docs.sigil.build/dictum/guides/languages/).
- **Profiles.** Several sets of settings, each with its own hotkey, languages, words and processing. See [Profiles](https://docs.sigil.build/dictum/guides/profiles/).
- **Clean-up with your own LLM.** Optionally, an OpenAI-compatible server you run, such as llama.cpp's `llama-server` or Ollama, fixes punctuation and slips. See [LLM clean-up](https://docs.sigil.build/dictum/guides/llm-cleanup/).
- **Your words and replace rules.** Teach Dictum names and terms it gets wrong, and fix recurring mistakes with plain or regex rules. See [Your words and replace rules](https://docs.sigil.build/dictum/guides/words-and-rules/).
- **A desktop app and a command line.** `dictum-gui` is the tray app, with a setup wizard, Settings, history and the on-screen overlay. `dictum` is the [command line](https://docs.sigil.build/dictum/reference/cli/) and the background daemon that listens for the hotkey, and `dictum doctor` checks the microphone, models, GPU, hotkeys and pasting, each with a link to its fix in [Troubleshooting](https://docs.sigil.build/dictum/troubleshooting/).

## Documentation

The guides, the reference and the troubleshooting are at [docs.sigil.build/dictum](https://docs.sigil.build/dictum/). In Ukrainian: [docs.sigil.build/uk/dictum](https://docs.sigil.build/uk/dictum/).

## Install

Download the package for your system from [GitHub Releases](https://github.com/Sigil-build/dictum/releases): an MSI or a zip for Windows 11, an AppImage or a tarball for Linux (x86-64). For Arch and CachyOS, use the `PKGBUILD` attached to each release, which carries the tarball's checksum.

The steps for each package, the requirements and the first run are in [Getting started](https://docs.sigil.build/dictum/getting-started/).

## Verify your download

The packages are not code-signed yet. Each release has `SHA256SUMS` and, in its notes, a link to a VirusTotal report on each package ([how to check](https://docs.sigil.build/dictum/getting-started/#check-download)). Releases after 0.4.0 also have a build attestation for each file, which `gh attestation verify <file> -R Sigil-build/dictum` checks. [SECURITY.md](SECURITY.md#verify-a-download) has the details, and how to report a vulnerability.

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
