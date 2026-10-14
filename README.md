# Dictum

Private voice-to-text utility: press a hotkey, speak, and the text lands in the focused field. Native AOT on .NET 10, with an Avalonia GUI (`dictum-gui`) and a CLI (`dictum`).

## Getting started

1. Put the `dictum` folder somewhere permanent (login start remembers its path).
2. Run `dictum init`. It asks for your language, picks a speech model for this machine, and downloads it.
3. Run `dictum daemon` (or let `init` set up login start), then hold `Ctrl+Alt+Space` and speak.
4. Words it gets wrong? `dictum vocab add "Notepad++" "VS Code"`.

`dictum doctor` checks everything and says what to fix.

## Building

Build with the .NET 10 SDK: `dotnet build`, then `dotnet test`. For a native binary, run `dotnet publish src/Dictum.Cli -c Release -r <rid>` (`linux-x64`, `win-x64` or `osx-arm64`).
