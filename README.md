# Dictum

Private voice-to-text utility: press a hotkey, speak, and the text lands in the focused field. Native AOT on .NET 10, with an Avalonia GUI (`dictum-gui`) and a CLI (`dictum`).

Build with the .NET 10 SDK: `dotnet build`, then `dotnet test`. For a native binary, run `dotnet publish src/Dictum.Cli -c Release -r <rid>` (`linux-x64`, `win-x64` or `osx-arm64`).
