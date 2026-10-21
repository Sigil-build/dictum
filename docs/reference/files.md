# Where Dictum keeps its files

Dictum keeps two folders for each user: one for your settings and one for its data. Updating or removing Dictum leaves both alone, so a reinstall picks up where you left off.

## The two folders {#folders}

| Folder | Windows | Linux |
| --- | --- | --- |
| Settings: `config.json` and your prompts | `%APPDATA%\Dictum` | `~/.config/dictum` |
| Data: models, history and logs | `%LOCALAPPDATA%\Dictum` | `~/.local/share/dictum` |

On Linux, Dictum follows `XDG_CONFIG_HOME` and `XDG_DATA_HOME` when they are set: the folders are then `$XDG_CONFIG_HOME/dictum` and `$XDG_DATA_HOME/dictum`.

To open a folder on Windows, paste its path, such as `%APPDATA%\Dictum`, into the address bar of File Explorer. On Linux, run `xdg-open ~/.config/dictum`. To print where the settings file is:

```sh
dictum config path
```

On Windows, run `dictum` by its full path: see [The command line](../getting-started.md#command-line).

## What is in them {#contents}

### The settings folder {#settings-folder}

| File | What it holds |
| --- | --- |
| `config.json` | All your settings: profiles, speech recognition, processors and the rest. Settings, the setup wizard and `dictum` commands such as `dictum vocab add` write it. |
| `prompts/` | Your prompts for LLM clean-up, one file each: `prompts/<name>.md`. When you edit the built-in `cleanup` prompt in Settings, your version is saved as `prompts/cleanup.md` and replaces it. |

### The data folder {#data-folder}

| File | What it holds |
| --- | --- |
| `models/` | The speech models, `ggml-*.bin`, and the voice activity detector. A speech model takes 265 MB to 875 MB. |
| `history.jsonl` | Your recent dictations, one per line: the text as Whisper heard it and as it was pasted. Never the audio. The **History** field in Settings → **General** sets how many it keeps: 200 at first, and 0 turns history off. |
| `logs/` | The log files: see [Logs](#logs). |
| `downloads.json` | Downloads that have not finished, so that they continue after a restart. |
| `setup.json` | The setup wizard's answers while its files download, so that a restart returns to the wizard. |
| `remote-desktop.token` | Linux on Wayland: the desktop's permission for Dictum to press Ctrl+V. Delete it, and the desktop asks again. |
| `tray-icons/` | Linux: the images of the tray icon. |
| `dictum.sock` | Windows: the socket through which the desktop app and `dictum` commands talk to the daemon while it runs. On Linux it is `$XDG_RUNTIME_DIR/dictum.sock`. |

## config.json {#config-json}

`config.json` holds every setting in one JSON file. Settings changes most of them for you; the file is there for what you would rather type, copy or keep in a backup.

### An example {#config-example}

This file has two profiles. `dictate` is held down to talk, recognises Ukrainian and English, and sends the text through the processor `llm`, a language model at `http://localhost:8080/v1`, before it pastes it. `toggle` is pressed once to start and once to stop, and pastes the text as Whisper heard it.

```json
{
  "schemaVersion": 1,
  "general": {
    "historySize": 200,
    "sounds": true,
    "pasteDelayMs": 150,
    "overlay": {
      "enabled": true,
      "position": "bottom",
      "size": "medium",
      "opacity": 1.0,
      "palette": "aurora"
    }
  },
  "stt": {
    "local": {
      "type": "whisper-local",
      "model": "ggml-large-v3-turbo-q8_0.bin",
      "vad": true,
      "vadModel": "ggml-silero-v6.2.0.bin",
      "runtime": "auto"
    }
  },
  "processors": {
    "llm": {
      "type": "llm",
      "baseUrl": "http://localhost:8080/v1",
      "model": "qwen3-4b",
      "prompt": "cleanup",
      "apiKey": "secret:dictum-processor-llm",
      "timeoutMs": 15000,
      "onError": "passthrough"
    }
  },
  "profiles": [
    {
      "id": "dictate",
      "name": "Dictate",
      "hotkey": "Ctrl+Alt+Space",
      "mode": "PushToTalk",
      "stt": "local",
      "language": "uk, en",
      "vocabulary": ["Dictum", "Whisper", "llama.cpp"],
      "pipeline": ["llm"],
      "output": "paste"
    },
    {
      "id": "toggle",
      "name": "Toggle",
      "hotkey": "Ctrl+Alt+D",
      "mode": "Toggle",
      "stt": "local",
      "language": "uk, en",
      "pipeline": [],
      "output": "paste"
    }
  ]
}
```

When Dictum saves the file, it writes every setting, with the defaults this example leaves out, so your file is longer.

### What the keys mean {#config-keys}

The notes are here, below the file, because the example is plain JSON that you can copy as it is.

`schemaVersion`
:   The version of the file's layout. Dictum 0.4 writes `1`; leave it as it is.

`general`
:   Settings for every profile.

    - `historySize`: how many recent dictations to keep; 0 keeps none.
    - `sounds`: a short tick when recording starts and stops.
    - `pasteDelayMs`: how long Dictum waits after it presses Ctrl+V before it puts back what the clipboard held, from 0 to 5000 milliseconds.
    - `overlay`: the listening overlay that the desktop app draws: `enabled`, `position` (`bottom` or `top`), `size` (`small`, `medium` or `large`), `opacity` (0.3 to 1.0) and `palette` (`aurora`, `ember` or `mist`).
    - Not in the example: `audioDevice`, the microphone's name, where none means the system's default microphone; `uiLanguage`, `"en"` or `"uk"`, where none follows the system; `injector`, how Dictum presses Ctrl+V on Wayland: `auto` tries the RemoteDesktop portal, then `ydotool`, then `wtype`; `terminalApps`, the apps that get Ctrl+Shift+V, by process or window class name, on Windows and X11.

`stt`
:   The speech recognition providers. Each has an id of your choice, here `local`, which profiles name.

    - `type`: `whisper-local` runs Whisper on your computer; `openai-compatible` sends the recording to a server, with `baseUrl`, `model`, `apiKey` and `timeoutMs` (60000 if you leave it out).
    - `model`: the model's file name in the `models` folder, or a full path.
    - `vad` and `vadModel`: the voice activity detector, which finds the speech in a recording, so that a recording without speech gives no text.
    - `runtime`: `auto` uses the graphics card when it can and the processor otherwise; `cpu` always uses the processor.
    - Not in the example: `threads`, the processor threads Whisper uses, where 0 chooses from the number of cores.

`processors`
:   The steps that change the text after recognition, each with an id of your choice, here `llm`. A profile runs the ones its `pipeline` names. Dictum warns about a processor that no profile runs.

    - `type`: `llm` sends the text to a language model; `replace` applies your replace rules, which are in `rules`. See [Your words and replace rules](../guides/words-and-rules.md).
    - `baseUrl`: the server's address, with the API's version, such as `/v1`. Any server with the OpenAI chat API works: llama-server, llama-swap, Ollama.
    - `model`: the model's name on that server.
    - `prompt`: `cleanup` is the built-in prompt that fixes punctuation and typos. Another name reads `prompts/<name>.md` in the settings folder.
    - `apiKey`: where the server's key comes from: see [API keys in the keyring](#keyring).
    - `timeoutMs`: how long Dictum waits for the answer, in milliseconds: 4000 if you leave it out. The setup wizard writes 15000.
    - `onError`: what happens when the server fails or is too slow: `passthrough` pastes the text without this processor's changes, `fail` fails the dictation.
    - Not in the example: `temperature`, 0 to 2, 0.2 if you leave it out; `preserveText`, which rejects an answer that changes the text instead of fixing it, and is on for the `cleanup` prompt if you leave it out; `maxTokens`, the longest answer, which grows with the text if you leave it out.

    [LLM clean-up](../guides/llm-cleanup.md) sets this up step by step.

`profiles`
:   Your profiles, in order. `dictum` commands use the first one when you name none with `--profile`.

    - `id`: the profile's id, unique in the file. `name` is what the tray menu shows.
    - `hotkey`: the keys, such as `Ctrl+Alt+Space`: any of `Ctrl`, `Alt`, `Shift` and `Super`, and one other key. Each profile needs keys of its own.
    - `mode`: `PushToTalk` records while you hold the keys; `Toggle` starts at the first press and stops at the second.
    - `stt`: the id of an entry in `stt`.
    - `language`: one language code, such as `uk`; a list, such as `uk, en`, where the first is the main language; or `auto` to detect any language. See [Languages](../guides/languages.md).
    - `vocabulary`: your words, which Whisper and the language model should spell as you wrote them.
    - `pipeline`: the ids of the processors to run, in order. Empty, the text is pasted as Whisper heard it.
    - `output`: `paste` puts the text on the clipboard, presses Ctrl+V and puts back what the clipboard held; `type` types the text key by key; `clipboard` leaves the text on the clipboard.
    - Not in the example: `pasteShortcut`, `auto`, `ctrl+v` or `ctrl+shift+v`, for a terminal on Wayland; `sttPrompt`, a hint for Whisper on top of the words.

### Editing it by hand {#config-edit}

To open the file in an editor, run:

```sh
dictum config edit
```

It opens the editor that `VISUAL` or `EDITOR` names, or else Notepad on Windows and the default app for the file on Linux. When the editor closes, it checks the file. On Linux, the default app can return before you save, so check again yourself:

```sh
dictum config validate
```

The daemon and the desktop app notice when the file changes and load it. If the new file has an error, they keep the last settings that worked, and `dictum config validate` says what is wrong: see [Troubleshooting](../troubleshooting.md#config).

Dictum reads `//` comments and trailing commas, but drops them the next time it saves the file from Settings, the wizard or a `dictum` command.

## API keys in the keyring {#keyring}

An API key that you type in Settings, the setup wizard or `dictum init` does not go into `config.json`. Dictum keeps it in the system keyring and writes a reference in its place, such as `"apiKey": "secret:dictum-processor-llm"`.

Windows
:   Credential Manager, under Windows Credentials, as a generic credential named `dictum:<name>`, such as `dictum:dictum-processor-llm`.

Linux
:   The Secret Service: GNOME Keyring or KWallet. The item is labelled `Dictum: <name>` and has the attributes `application` = `dictum` and `name` = `<name>`.

The name is `dictum-processor-<id>` for a processor's key and `dictum-stt-<id>` for a speech server's key, where `<id>` is its id in `config.json`.

`apiKey` takes three forms:

- `secret:<name>` reads the key from the keyring;
- `env:<VARIABLE>`, such as `env:OPENAI_API_KEY`, reads it from an environment variable that the daemon can see;
- anything else is the key itself, in plain text. `dictum doctor` and `dictum config validate` warn about it.

To manage the keys from a terminal:

```sh
dictum secret set dictum-processor-llm      # asks for the key without showing it
dictum secret check dictum-processor-llm    # says whether it is stored, never prints it
dictum secret delete dictum-processor-llm
```

Removing Dictum leaves the keys in the keyring: [Start fresh](../guides/update-and-remove.md#start-fresh) says how to delete them. When the keyring does not answer, see [Troubleshooting](../troubleshooting.md#secrets).

## Logs {#logs}

The logs are in the `logs` folder inside the data folder: `%LOCALAPPDATA%\Dictum\logs` on Windows, `~/.local/share/dictum/logs` on Linux.

`dictum.log`
:   The daemon and the `dictum` commands.

`dictum-gui.log`
:   The desktop app, with what the daemon it started prints.

When a file grows past 1 MB, Dictum renames it, `dictum.log` to `dictum.log.1`, and starts a new one. It keeps three older files, `.1` to `.3`.

The logs say what Dictum did and what went wrong, but not what you said: a dictated text appears as its length, such as `<42 chars>`. Only `--verbose` writes the text itself: `dictum daemon --verbose` prints details, the dictated text included, to the terminal and to the log. [Troubleshooting](../troubleshooting.md#logs) says what to look for.

## Programs and login items {#programs}

Besides the two folders, Dictum's files are where its package put them:

| Package | Programs |
| --- | --- |
| Windows installer | `%LOCALAPPDATA%\Programs\Dictum` |
| Windows zip | the folder you chose, such as `C:\Tools\Dictum` |
| AppImage | the AppImage file |
| Tarball | `~/.local/lib/dictum`, with links in `~/.local/bin` and a menu entry in `~/.local/share/applications/dictum.desktop` |
| Arch package | `/opt/dictum`, with links in `/usr/bin` |

And where start at login and the desktop portal look:

| What | Where |
| --- | --- |
| Start at login on Windows | the value `Dictum` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| Start at login on Linux, the desktop app | `~/.config/autostart/dictum.desktop` |
| Start at login on Linux, the daemon alone | the systemd user unit `~/.config/systemd/user/dictum.service` |
| The portal's entry on Wayland | `~/.local/share/applications/dev.dictum.Dictum.desktop`, hidden from menus |

[Start at login](../guides/start-at-login.md) explains the two kinds of login item.

## Other folders {#environment}

These environment variables point Dictum elsewhere, for a second setup or a script:

`DICTUM_CONFIG_DIR`
:   The settings folder.

`DICTUM_DATA_DIR`
:   The data folder.

`DICTUM_CONFIG`
:   A settings file in place of `config.json`. The `--config` option of a `dictum` command wins over it.

Set them for the desktop app too: the daemon it starts inherits its environment. A login item does not carry them.
