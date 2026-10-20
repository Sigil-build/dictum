# Dictum

Dictum turns speech into text on Windows 11 and Linux. Hold a hotkey, speak, let go, and the text appears where your cursor is, in the app you are typing in: an editor, a browser, a chat or a terminal.

Speech is recognised on your computer, with Whisper. What you say does not leave it, unless you connect a server yourself: for recognition, or for clean-up with a language model.

Dictum is free and open source, under the [MIT License](https://github.com/Sigil-build/dictum/blob/HEAD/LICENSE).

## How it works {#how-it-works}

1. **You hold the hotkey and speak.** The hotkey is `Ctrl+Alt+Space` unless you choose another one. Dictum plays a short sound and records from the microphone only while you hold the keys, and a small overlay at the bottom of the screen shows that it is listening. A profile set to "Toggle" works differently: press the hotkey once to start and once more to stop.
2. **You let go.** Whisper, a speech recognition model, turns the recording into text on your computer: on the graphics card through Vulkan when there is one, otherwise on the processor.
3. **The profile's processors work on the text**, if it has any, in the order you set. Replace rules fix words that keep coming out wrong, and an LLM prompt sends the text to a language model on a server you connect, which fixes the punctuation and typos.
4. **Dictum pastes the text.** It puts the text on the clipboard, presses Ctrl+V for you (Ctrl+Shift+V in a terminal it recognises), then puts back the text the clipboard held before. If the clipboard held something other than text, such as an image, the dictation replaces it.

Dictum keeps your recent dictations: "History" in the tray menu shows them, so you can copy one again.

A *profile* holds these choices: the hotkey, the languages, your words and the processors. You can have several profiles, each with its own hotkey.

## Two programs {#two-programs}

Dictum installs two programs that work together:

- **`dictum-gui`** is the desktop app: the tray icon and its menu, the setup wizard, Settings and the listening overlay. It starts the daemon, and starts it again if it stops.
- **`dictum`** is the command line. `dictum daemon` is the background process that does the work: it listens for the hotkeys, records, recognises the speech and pastes the text. The other commands check and change Dictum from a terminal, for example `dictum status` and `dictum doctor`.

Everyday use needs only the desktop app. If you prefer the terminal, `dictum init` sets Dictum up there, and `dictum daemon` runs it without the desktop app, so without the tray icon and the overlay.

## Where to go next {#next}

- [Getting started](getting-started.md): install Dictum, set it up and dictate for the first time.
- Guides, each from start to finish:
  - [Languages](guides/languages.md): dictate in more than one language, and what the main language is for.
  - [Profiles](guides/profiles.md): several setups, each with its own hotkey.
  - [LLM clean-up](guides/llm-cleanup.md): let a language model on a server you choose fix punctuation and typos.
  - [Your words and replace rules](guides/words-and-rules.md): teach Dictum names and terms, and fix words that come out wrong.
  - [Start at login](guides/start-at-login.md).
  - [Update and remove](guides/update-and-remove.md).
- [Troubleshooting](troubleshooting.md): when something does not work, start with `dictum doctor`.
- Reference: [Settings](reference/settings.md), the [setup wizard](reference/setup-wizard.md), the [tray and overlay](reference/tray-and-overlay.md), the [command line](reference/cli.md) and the [files Dictum keeps](reference/files.md).
- [Privacy](privacy.md): what Dictum uses on your computer, and when.
