# Dictum

Voice-to-text utility. Implementation plan (lives outside the repo, read it before feature work):
https://claude.ai/code/artifact/ba039eea-3640-41cc-898a-b1b024e07370

## Stack
.NET 10 + Native AOT · Avalonia 12 + CommunityToolkit.Mvvm · System.CommandLine 2.x · Microsoft.Extensions.DependencyInjection · Whisper.net · SharpHook · Tmds.DBus.Protocol · xUnit v3. Package versions live only in `Directory.Packages.props` (Central Package Management).

## AOT rules
- Warnings are errors, IL2xxx/IL3xxx included. Never suppress them; fix the cause or raise it.
- JSON only through a source-generated `JsonSerializerContext`.
- DI: explicit registrations only, no assembly scanning or open-generic factories.
- XAML: compiled bindings, `x:DataType` on every view, no runtime XAML loading, ViewLocator via `switch`.
- Native interop: `[LibraryImport]`, not `[DllImport]`. D-Bus via Tmds.DBus.Protocol.
- No `Assembly.Load` plugins. External providers run as a process or HTTP service.
- `PublishAot` is set only in `Dictum.App` and `Dictum.Cli`. Libraries set `IsAotCompatible`.

## Commands
- Build: `dotnet build`
- Test: `dotnet test`
- Publish CLI: `dotnet publish src/Dictum.Cli -c Release -r linux-x64` (also `win-x64`, `osx-arm64`)
- Publish GUI: `dotnet publish src/Dictum.App -c Release -r <rid>`
- Native AOT cannot cross-compile between OSes. Publish on the target OS or in a container.

## Layout
- `src/Dictum.Core`: contracts, pipeline, profiles, history. No references and no OS code.
- `src/Dictum.Audio`, `Dictum.Stt.Whisper`, `Dictum.Stt.OpenAi`, `Dictum.Processing`: reference Core.
- `src/Dictum.Platform`: platform abstractions (hotkeys, injection, clipboard, secrets, autostart). References Core.
- `src/Dictum.Platform.{Windows,Linux,MacOS}`: per-OS implementations. Reference Platform.
- `src/Dictum.Host`: composition root, provider registry, IPC server. References everything above.
- `src/Dictum.App`: Avalonia tray UI, exe `dictum-gui`. `src/Dictum.Cli`: CLI + IPC client, exe `dictum`. Both reference Host.
- `tests/Dictum.Core.Tests`: unit tests on fakes. `tests/Dictum.Integration.Tests`: real STT/LLM, gated by `DICTUM_INTEGRATION=1`.

## Conventions
English for code, comments, docs and commits. Keep the repo lean: no docs/ folder, ADRs or copies of the plan.
