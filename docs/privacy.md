# Privacy

Dictum works on your computer. This page says what it uses there, when, and what leaves your computer. Every statement here comes from what the code of Dictum 0.4 does.

## In short {#summary}

| What | When Dictum uses it | Where it goes |
| --- | --- | --- |
| The microphone | While you dictate, while **Settings → General** is open for its level meter, and during a provider **Test** | Nowhere, with local Whisper. To a speech server only if a profile uses one. |
| The keyboard | Listens for your hotkeys while the daemon runs; presses Ctrl+V to paste | Nowhere: no key is stored or logged. |
| The clipboard | While it pastes: saves the text that was there, and puts it back | Nowhere. |
| The keyring | Only for API keys written as `secret:<name>` | Sent only to the server that key is for. |
| The network | Only the servers you add, and model downloads from Dictum's catalog | The servers you chose, and Hugging Face for models. |
| History and logs | After each dictation, and while Dictum runs | Files on your computer. |

Dictum sends no telemetry: no usage statistics, no crash reports, no update checks.

## Microphone {#microphone}

Dictum opens the microphone only at these times, and closes it right after:

- **While you dictate:** from the moment you press the hotkey until you let it go. A profile set to **Toggle** records from one press to the next. `dictum start` and `dictum toggle` start a recording the same way; `dictum stop` and `dictum cancel` end it.
- **While Settings → General is open:** the level meter under **Microphone** reads the microphone's level. It keeps no sound, only the level. It stops when you open another page or close Settings. If the desktop app quits without stopping it, the daemon stops it within about a minute.
- **When you click Test** on a speech provider in **Settings → Speech providers:** it records five seconds. **Test with a sample** uses a recording built into Dictum and does not open the microphone.
- **On the command line:** `dictum record` records until you press Enter, Ctrl+C or Esc. `dictum stt test` records five seconds, unless you give it a file with `--file`.

The setup wizard does not open the microphone. Listing the microphones, as `dictum devices`, `dictum doctor` and Settings do, records nothing.

A recording stays in memory until it is turned into text, and is never written to disk. The one exception is `dictum record --save-audio <file>`, which saves it to the file you name. History keeps the text, never the sound.

With a local Whisper provider, the sound never leaves your computer. If a profile uses a remote server for speech recognition, each of its recordings goes to that server: see [Network](#network).

## Keyboard {#keyboard}

**Listening for hotkeys.** While the daemon runs, it waits for the hotkeys of your profiles:

- **On Windows and X11**, Dictum uses SharpHook: a low-level keyboard hook on Windows, the XRecord extension on X11. The hook sees every key you press, in every app. Dictum compares each key with your hotkeys and does nothing else with it: it does not store, send or log the keys. On Windows it keeps its own hotkey from reaching the app you type in. **Pause all hotkeys** in the tray menu removes the hook, so nothing in Dictum sees your keys until you click **Resume hotkeys** or the daemon restarts. Dictum also runs no hook while no profile has a hotkey.
- **On Wayland**, Dictum uses the desktop's GlobalShortcuts portal. Dictum sees no keys at all: the desktop tells it only when one of Dictum's own shortcuts is pressed and released. The desktop asks you before it gives Dictum a shortcut.

**Pressing Ctrl+V.** To paste, Dictum presses Ctrl+V, or Ctrl+Shift+V in a terminal, for you. It presses nothing else.

- On Windows it uses `SendInput`, and on X11 the XTest extension.
- On Wayland it uses the desktop's RemoteDesktop portal, `ydotool` or `wtype`. The portal asks you once whether Dictum may control the keyboard. To avoid asking again, Dictum keeps a token in `~/.local/share/dictum/remote-desktop.token`, which only your user can read. Delete the file to make the desktop ask again.

**The focused app.** To know when to paste with Ctrl+Shift+V, Dictum reads the name of the app that has the focus: its process name on Windows, its window class on X11 (through `xprop`). It reads it when it pastes, and when a recording stops if the profile has processors. On Wayland, Dictum cannot see the focused app. The name goes nowhere, unless you write your own LLM prompt that contains `{{app}}`: then that prompt sends it to the server. The built-in prompts do not use it.

## Clipboard {#clipboard}

Dictum pastes through the clipboard. For each dictation it:

1. reads the text on the clipboard, if there is text;
2. puts the dictation on the clipboard;
3. on Windows and X11, waits until you let go of the hotkey's modifier keys, for up to two seconds;
4. presses Ctrl+V;
5. waits for **Clipboard restore delay, ms**, 150 ms unless you change it in **Settings → General → Pasting settings ›**;
6. puts the earlier text back, unless the clipboard changed in the meantime.

Only text is saved and put back. If the clipboard held an image or files, the dictation replaces them. If the clipboard was empty, the dictation stays on it. A profile with **Copy to clipboard only** leaves the dictation on the clipboard and puts nothing back.

Clipboard managers, and Windows clipboard history (Win+V) when it is on, can keep a copy of each dictation. On Wayland, Dictum marks the text as sensitive (`wl-copy --sensitive`, in wl-clipboard 2.3 and later), which asks clipboard managers not to keep it. On Windows and X11 there is no such mark.

On Linux, Dictum reads and writes the clipboard through `wl-copy` and `wl-paste` on Wayland, and `xclip` or `xsel` on X11.

## API keys and the keyring {#keyring}

Dictum uses the system keyring for API keys, and for nothing else: Windows Credential Manager on Windows, the Secret Service (GNOME Keyring or KWallet) on Linux.

- **When it writes:** when you type a key in Settings or in the setup wizard, or run `dictum init` or `dictum secret set <name>`. The key is stored as `dictum:<name>` in Credential Manager, or as an item named `Dictum: <name>` in the Secret Service. The config then holds only `secret:<name>`, never the key.
- **When it reads:** only when a config value says `secret:<name>`, at the moment a request to that server needs the key: a dictation, a test, or a check by `dictum doctor` or **Settings → System**. The key goes only to that server, as a Bearer token.
- **When it deletes:** when you remove the provider or processor and no other one uses the key, or when you run `dictum secret delete <name>`.

Dictum never shows a stored key, and never writes one to its logs. A key can also come from an environment variable, written as `env:<VAR>`. A key written in the config as plain text works too, but Dictum warns about it.

## Network {#network}

Dictum connects to these servers, and to no others:

- **Speech servers you add** in **Settings → Speech providers** as **Remote server**. For each dictation in a profile that uses one, Dictum sends the recording as a WAV file, with the model name, the language and the profile's speech hint, which includes your words, to `<URL>/audio/transcriptions`.
- **LLM servers of your processors.** For each dictation in a profile whose chain has an LLM prompt, Dictum sends the prompt to `<URL>/chat/completions`: the recognised text and its language, and, in prompts you write yourself, the focused app's name and your words if the prompt asks for them. Settings sends text there too: the **Preview** of an LLM prompt in **Settings → Processors** sends its **Sample sentence** 600 ms after each change, while the editor is on screen, and **History** sends an entry's recognised text when you run it through a chain again.
- **Checks of those servers.** `dictum doctor`, **Settings → System** and the server test in the setup wizard ask `<URL>/models`. A speech server without that address gets half a second of silence instead. A provider's **Test** in **Settings → Speech providers** sends five seconds from the microphone, or Dictum's built-in sample.
- **Model downloads.** Dictum downloads speech models only from the addresses in the catalog built into it, which are fixed versions of files on Hugging Face. It does so only when you choose a model in the setup wizard or in **Settings → Models**, or run `dictum init` or `dictum models pull`. Before Dictum uses a file, it checks the file's SHA-256 against the catalog, and deletes a download that does not match.

A profile with a local Whisper provider and no LLM prompt sends nothing over the network.

The desktop app and the daemon talk to each other through a local socket file, which is not open to the network.

## No telemetry {#telemetry}

Dictum does not collect or send usage statistics, crash reports or analytics, and does not check for updates. The connections under [Network](#network) are the only ones in its code.

**Documentation** in the tray menu, **Help** in Settings, **How does Dictum work?** in the setup wizard and **How to fix** in **Settings → System** open pages of docs.sigil.build in your browser, and `dictum doctor` prints links to them: that connection is made by your browser, not by Dictum.

## History {#history}

Dictum keeps your recent dictations, so that you can copy one again.

- **Where:** `history.jsonl`, in `%LOCALAPPDATA%\Dictum` on Windows and `~/.local/share/dictum` on Linux. It stays on your computer.
- **What:** for each dictation, the time, the profile, the speech provider, the recognised text, the final text, the language, the length of the recording, the model, where it ran, and the time recognition took. When a dictation fails after recognition, the recognised text is kept with the error, so it is not lost. No sound is kept.
- **How many:** the last 200, unless you change the number in the **History** row of **Settings → General**. When you lower the number, the file is cut to that size at the next dictation.
- **Turn it off:** set the **History** number to 0. Dictum then adds nothing to the file. The entries already in it stay until you delete them.
- **Delete one:** open **History** from the tray menu, select the entry, and click **Delete**.
- **Delete all:** quit Dictum from the tray menu, then delete `history.jsonl`.

## Logs {#logs}

Dictum writes what it does to log files in the `logs` folder: `%LOCALAPPDATA%\Dictum\logs` on Windows, `~/.local/share/dictum/logs` on Linux.

- **What:** the hotkeys, the backends, the timings, and the errors with the messages the servers sent. The text you dictate shows only as its length, such as `<42 chars>`.
- **The exception:** `--verbose`, as in `dictum daemon --verbose`, writes the dictated text itself into the log. Use it only to find a problem.
- **How much:** when a log file passes 1 MB, Dictum starts a new one and keeps three old ones.
- **Delete them:** the logs cannot be turned off. Quit Dictum from the tray menu, then delete the `logs` folder.

[Troubleshooting](troubleshooting.md#logs) shows how to read the logs.

## Other files {#files}

Your settings are in `config.json` in `%APPDATA%\Dictum` on Windows and `~/.config/dictum` on Linux, with your own prompts in its `prompts` folder. They hold your words, your replace rules and the addresses of your servers. The models, the history and the logs are in `%LOCALAPPDATA%\Dictum` or `~/.local/share/dictum`. Uninstalling Dictum keeps both folders; delete them to remove everything. [The files Dictum keeps](reference/files.md) lists each file.
