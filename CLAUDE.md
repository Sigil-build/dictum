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
- `Directory.Build.targets` holds the only agreed AOT exceptions (x86-64-v3, Whisper.net IL3000, native pruning); read its comments before changing it.
- `Directory.Build.props` turns off CS1591 and CS1573: the build generates XML docs so IDE0005 (unused usings) works, without asking for docs on every member. Besides the AOT ones in `Directory.Build.targets`, they are the only agreed analyzer exceptions.

## Commands
- Build: `dotnet build`
- Test: `dotnet test`
- Regenerate `docs/reference/cli.md` after a CLI change: `DICTUM_WRITE_DOCS=1 dotnet test --project tests/Dictum.Cli.Tests --filter-class "*.CliReferenceTests"`, then commit it.
- Publish CLI: `dotnet publish src/Dictum.Cli -c Release -r linux-x64` (also `win-x64`, `osx-arm64`)
- Publish GUI: `dotnet publish src/Dictum.App -c Release -r <rid>`
- Docs: pages live in `docs/` and go live from a `docs/X.Y` branch, which `.github/workflows/docs.yml` cuts from the `vX.Y.Z` tag when a release is published (a patch release or a pre-release cuts nothing). Fix a typo on `docs/X.Y`, then merge it back into `main`.
- Native AOT cannot cross-compile between OSes. Publish on the target OS or in a container.
- Check the user docs (twins, heading ids, links, nav, version): `dotnet test --project tests/Dictum.Core.Tests --filter-class "*.UserDocsTests"`

## Layout
- `src/Dictum.Core`: contracts, pipeline, profiles, history. No project references, no interop or OS APIs; plain .NET `OperatingSystem` branches are fine.
- `src/Dictum.Audio`, `Dictum.Processing`: reference Core. `src/Dictum.Stt.Whisper`, `Dictum.Stt.OpenAi`: reference Core and Audio.
- `src/Dictum.Setup`: model catalog, downloader, setup planner and runner (`dictum init`). References Core only.
- `src/Dictum.Platform`: platform abstractions (hotkeys, injection, clipboard, secrets, autostart) and the SharpHook implementations that Windows and X11 share. References Core.
- `src/Dictum.Platform.{Windows,Linux}`: per-OS implementations. Reference Platform. macOS is unsupported and has no project: it gets Platform's `UnsupportedPlatform`.
- `src/Dictum.Host`: composition root, daemon, IPC server. Core defines the `ProviderRegistry`; Host fills it. References everything above.
- `src/Dictum.App`: Avalonia tray UI, exe `dictum-gui`. `src/Dictum.Cli`: CLI + IPC client, exe `dictum`. Both reference Host.
- `tests/Dictum.Core.Tests`: unit tests on fakes for the libraries under Host, and `ArchitectureTests`, which check this layout.
- `tests/Dictum.Host.Tests`: Host on fakes and temp folders (daemon, IPC, doctor, config watcher), plus Stt.Whisper's pure parts.
- `tests/Dictum.Cli.Tests`: the CLI's exit codes and init's checks, in-process, with its paths and socket in a temp folder.
- `tests/Dictum.App.Tests`: the desktop app on fakes and Avalonia.Headless; the real-daemon test is gated by `DICTUM_INTEGRATION=1`.
- `tests/Dictum.Integration.Tests`: real STT/LLM tests gated by `DICTUM_INTEGRATION=1`, the catalog check gated by `DICTUM_CHECK_CATALOG=1`, and tests against the current OS (clipboard, Credential Manager, registry, tray, D-Bus).
- `tests/Dictum.Testing`: the fakes and helpers the test projects share; a library, not a test project.
- `docs/`: the user documentation, each page in English and in Ukrainian (`*.uk.md`), and `site.yml` with the nav and version for docs.sigil.build; `UserDocsTests` in Core.Tests checks its structure.

## Conventions
English for code, comments, docs and commits. Keep the repo lean: `docs/` holds only the user documentation published on docs.sigil.build (en and uk); no ADRs, plans or copies of the plan.
