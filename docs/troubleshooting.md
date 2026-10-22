# Troubleshooting

When something does not work, start with `dictum doctor`, or with **Settings → System** in the desktop app. Both run the same checks, one row per part of Dictum. Find the row that is not OK, then read its section below.

## Run dictum doctor {#doctor}

**In the desktop app:** open **Settings** from the tray menu, then **System**. The checks run each time the page opens. **Check again** runs them once more.

**In a terminal:**

=== "Windows 11"

    `dictum.exe` is not on `PATH`. In PowerShell, run it by its full path:

    ```powershell
    & "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" doctor
    ```

    If you unzipped Dictum instead of installing the MSI, use the folder you unzipped into, for example `& "C:\Tools\Dictum\dictum.exe" doctor`.

=== "Linux"

    ```sh
    dictum doctor
    ```

    In the AppImage, put `dictum` after the file name:

    ```sh
    ./dictum-0.5.1-linux-x64.AppImage dictum doctor
    ```

Each row starts with `OK`, `WARN` or `ERR`, then the check's name and what it found. A row that is not OK has two more lines: after `→`, what to do; after `see:`, the link to its section on this page. The link opens the page in the language that **UI language** sets in **Settings → General**. With **Same as the system**, that is Ukrainian on a Ukrainian system and English on any other. The first line of the output names Dictum's version and your system. A part of the output on Windows:

```text
  OK config                 C:\Users\you\AppData\Roaming\Dictum\config.json (1 STT, 1 processors, 1 profiles)
WARN config warnings        1 warning(s)
                            → processors.cleanup: no profile's pipeline runs it, so dictation never uses it
                            see: https://docs.sigil.build/dictum/troubleshooting/#config-warnings
  OK models                 C:\Users\you\AppData\Local\Dictum\models (1 configured file(s) present)
  OK gpu                    NVIDIA GeForce RTX 3060, 12.0 GB VRAM
  OK audio library          PortAudio V19.7.0-devel, revision … (bundled portaudio.dll)
  OK microphone             Microphone (USB Audio Device) (Windows WASAPI, 48000 Hz); 2 input device(s)
  OK hotkeys                low-level keyboard hook (SharpHook)
  OK paste                  Win32 clipboard + SendInput
  OK secrets                Windows Credential Manager
  OK daemon                 running (pid 8412), Idle; 1 hotkey(s) registered via low-level keyboard hook (SharpHook)
  OK stt:local              ggml-large-v3-turbo-q8_0.bin, runtime Vulkan on NVIDIA GeForce RTX 3060 (DiscreteGpu)
  OK processor:cleanup      http://localhost:8080/v1 reachable, model qwen3-4b, prompt 'cleanup'
```

`dictum doctor` exits with code 1 when any row is `ERR`, and 0 otherwise. `dictum doctor --json` prints the same rows as JSON, with `level` set to `ok`, `warning` or `error`, and `docs` set to the link for a row that is not OK. Each provider check gets up to 15 seconds, so the command can take a while.

**Settings → System** shows the same rows with a badge (**OK**, **Warning** or **Error**), the check's name in your language, what it found, and what to do. The texts of the checks themselves are in English, in the app and in the terminal. The page adds two rows of its own: **Listening overlay** and **Tray icon**. Each of the other rows that is not OK has **How to fix** under its status: it opens the row's section of this page in your browser, the one the `see:` line links to, in the same language. The [Settings reference](reference/settings.md#system) describes the page.

Find your row here:

| `dictum doctor` | Settings → System | Section |
| --- | --- | --- |
| `config` | **Config file** | [Config file](#config) |
| `config warnings` | **Config warnings** | [Config warnings](#config-warnings) |
| `models` | **Model files** | [Model files](#models) |
| `audio library`, `microphone` | **Audio library**, **Microphone** | [Microphone](#microphone) |
| `hotkeys` | **Global hotkey** | [Hotkeys](#hotkeys) |
| `paste`, `paste (portal)`, `paste (ydotool)`, `paste order`, `clipboard`, `active window` | **Paste**, **Paste (portal)**, **Paste (ydotool)**, **Paste order**, **Clipboard**, **Active window** | [Pasting](#pasting) |
| `elevation` | **Administrator rights** | [Administrator rights](#administrator) |
| `secrets` | **Secret store** | [API keys and the keyring](#secrets) |
| `session`, `platform` | **Desktop session**, **Platform** | [Desktop session](#desktop-session) |
| `daemon` | **Daemon** | [Daemon](#daemon) |
| `gpu`, `stt:<id>` | **GPU**, **Speech provider &lt;id&gt;** | [Speech recognition](#speech-recognition) |
| `processor:<id>` | **Processor &lt;id&gt;** | [LLM clean-up](#llm-cleanup) |
| — | **Tray icon** | [No tray icon](#tray-icon) |
| — | **Listening overlay** | [No overlay](#overlay) |

Some problems show in no row: words that come out [translated](#translated-words), and [SmartScreen](#smartscreen) on Windows. When nothing here helps, read the [logs](#logs).

## Config file {#config}

The `config` row (**Config file**) reads `config.json`: `%APPDATA%\Dictum\config.json` on Windows, `~/.config/dictum/config.json` on Linux.

`No config at …` → `Run 'dictum init'.`
:   Dictum is not set up yet. Start the desktop app: its setup window walks you through it. In a terminal, run `dictum init`.

`Config is invalid: …` → `Fix the file, then run 'dictum config validate'.`
:   The file has errors. The row lists each one with its place in the file, such as `profiles[0].hotkey: …` or `processors.cleanup.baseUrl: '…' is not an absolute http(s) URL`.

`Config is not valid JSON for Dictum: …`
:   The file is not valid JSON, usually after an edit by hand. The row names the line, as in `line 12: …`.

`Config schema version 2 is newer than this build supports (1). Update Dictum.`
:   A newer Dictum wrote this file. Install that version again.

Settings and the setup wizard always write a valid file, so these errors come from edits by hand. To fix one:

1. Open the file in your editor with `dictum config edit`, or print its path with `dictum config path`.
2. Fix the line the error names, and save.
3. Run `dictum config validate`. It prints `OK: …` when the file is valid, and each warning on a line of its own.

While the daemon runs, it reads the file again each time you save it. A file with errors is not applied: the daemon keeps working with the previous settings, and on Linux it shows the notification "Dictum: config not applied".

## Config warnings {#config-warnings}

The `config warnings` row (**Config warnings**) shows only when the file has warnings. Dictum works with them, but something will not do what you expect. The row counts them, and its second line lists them, separated by `;`. `dictum config validate` prints them one per line.

| Warning | What to do |
| --- | --- |
| `processors.<id>: no profile's pipeline runs it, so dictation never uses it` | See [A processor in no profile](#processor-in-no-profile). |
| `profiles[0].language: 'uk' and 'ru' sound alike to Whisper; with both in the list it may pick the wrong one. Keep only the languages you dictate in.` | Remove the language you do not dictate in, or give it a profile of its own. See [Translated words](#translated-words). |
| `profiles[0].language: '<provider>' takes a single language, so with a list the server gets the first language, '<code>'` | A remote speech server takes one language. Keep one language in this profile, or use a profile per language. |
| `profiles[0].vocabulary: a word list can pull language detection towards its own language; set 'language' for this profile` | In **Settings → Profiles**, under **Languages**, pick **One** or **Several** instead of **Auto**. |
| `profiles[0].vocabulary: the speech hint holds 400 characters; left out: …` | Your words and the speech hint do not fit in what Whisper reads. Shorten the list; the words named were left out. |
| `profiles[0].vocabulary[3]: '…' is listed twice` | Remove the second copy. |
| `profiles[0].output: typing is not supported, and the profile pastes instead; use "paste" or "clipboard"` | Earlier versions of Dictum let a profile ask for typing the text key by key, which never worked. Such a profile now pastes. To clear the warning, change anything in that profile in **Settings → Profiles**, which saves it with **Paste into the focused app**, or run `dictum config edit` and write `"output": "paste"` for that profile. |
| `processors.<id>.rules[0].pattern: contains a backspace character: …` | In JSON, `"\b"` is a backspace. Write `"\\b"` for a word boundary. |
| `stt.<id>.apiKey: API key is stored in plain text; use 'env:VAR' or 'secret:name' instead` (or `processors.<id>.apiKey`) | Type the key again in Settings, which moves it to the keyring. See [API keys and the keyring](#secrets). |
| `general.historySize: is above 5000, the most Settings offers` | Settings shows at most 5000. Lower the number in the **History** row of **Settings → General**. |

### A processor in no profile {#processor-in-no-profile}

A processor, such as an LLM prompt or a set of replace rules, works only in the profiles whose processing chain includes it. One that no profile runs does nothing, and doctor warns:

```text
WARN config warnings        1 warning(s)
                            → processors.cleanup: no profile's pipeline runs it, so dictation never uses it
                            see: https://docs.sigil.build/dictum/troubleshooting/#config-warnings
```

In **Settings → Processors**, such a processor is marked **In no profile** in the list. When you open it, the page says "This processor runs in no profile, so dictation never uses it."

To use it:

1. Open **Settings → Processors** and select the processor.
2. Click **Add to profile…** and choose the profile.

Or open **Settings → Profiles**, select the profile, and under **Processing chain** click **Add a step**. In `config.json`, add the processor's id to the profile's `pipeline`, as in `"pipeline": ["cleanup"]`.

If you do not need it, select it and click **Remove processor**.

## Model files {#models}

The `models` row (**Model files**) checks that the speech model files your providers use are in the models folder: `%LOCALAPPDATA%\Dictum\models` on Windows, `~/.local/share/dictum/models` on Linux.

`Missing in …: ggml-large-v3-turbo-q8_0.bin` → `Run 'dictum models pull whisper-large-v3-turbo-q8'.`
:   The file is not there: the download did not finish, or the file was deleted. Download it in **Settings → Models**, or run the command the row gives. `dictum models list` shows which models are installed.

`Missing in …: …` → `Run 'dictum init', or copy the files there.`
:   The config names a file that is not in Dictum's catalog, so Dictum cannot download it. Copy the file into the models folder, or choose a model from the catalog in **Settings → Models** or with `dictum init`.

A download resumes where it stopped, and the file is used only after its SHA-256 matches the catalog. If it does not match, Dictum deletes the download and says `… failed the integrity check (SHA-256 …, expected …). The download was deleted; run the command again.` Run it again. If it fails each time, check that nothing on your network, such as a proxy, changes downloads.

## Microphone {#microphone}

The `audio library` row (**Audio library**) loads PortAudio, the library Dictum records with. The `microphone` row (**Microphone**) names the system's default microphone and counts the others.

`PortAudio could not be loaded: …`
:   On Linux, install the system package; the row's second line gives the command: `sudo pacman -S portaudio` (Arch), `sudo apt install libportaudio2` (Debian, Ubuntu) or `sudo dnf install portaudio` (Fedora). On Windows, reinstall Dictum: `portaudio.dll` must sit next to `dictum.exe`.

`No microphone found` → `Connect one, or check that the OS allows microphone access.`
:   No input device is visible to Dictum.

`Replaced by DICTUM_AUDIO_FILE=…`
:   The environment variable `DICTUM_AUDIO_FILE` makes Dictum read a file instead of the microphone. Unset it.

If the row is OK but dictation hears nothing or the wrong thing:

1. Open **Settings → General**. **Microphone** shows the one Dictum records from, which can differ from the system's default. Pick the right one.
2. Speak and watch the level meter under **Microphone**: green is good, red means clipping. `dictum devices` lists the microphones by name.
3. On Windows, open Windows Settings → Privacy & security → Microphone, and check that microphone access, and access for desktop apps, are on.
4. To hear what Dictum hears, record a test file and play it: `dictum record --save-audio test.wav`. Press Enter to stop.

## Hotkeys {#hotkeys}

The `hotkeys` row (**Global hotkey**) names how Dictum listens for hotkeys:

- `low-level keyboard hook (SharpHook)` on Windows;
- `X11 XRecord hook (SharpHook)` on X11;
- `org.freedesktop.portal.GlobalShortcuts v1`, the desktop's shortcut portal, on Wayland.

On Windows and X11 the row only names the hook. Whether each hotkey was registered shows elsewhere: in the `daemon` row (`… 1 hotkey(s) registered via …`), in `dictum status` (`registered` or `NOT registered` per profile), and in **Settings → Profiles**, which says "The daemon could not register this hotkey; another app may already use it." under a hotkey that failed.

If the hotkey does nothing:

1. Open the tray menu. If it shows **Resume hotkeys**, the hotkeys are paused: click it. While they are paused, the tray tooltip reads "Dictum · Hotkeys paused".
2. Check the profile's **Hotkey** in **Settings → Profiles**. Another app may use the same keys; click the field, then press a different combination.
3. Run `dictum status` and read the line for your profile, and the `warning:` line under it if there is one.

### Windows and X11 {#keyboard-hook}

`The keyboard hook failed to start: …`
:   On X11 the hook needs glibc 2.38 or newer (Ubuntu 24.04, Fedora 39, current Arch) and the libraries libXtst, libXt, libXrandr and libxkbcommon: `sudo apt install libxtst6 libxt6 libxrandr2 libxkbcommon0`. Otherwise bind `dictum toggle` in your desktop's shortcut settings, as below for Wayland.

`The keyboard hook did not start in time.`
:   Restart the daemon: quit Dictum from the tray menu and start it again.

`The previous keyboard hook has not stopped yet.`
:   Dictum removes the keyboard hook while the hotkeys are paused, or while no profile has a hotkey. This time the old hook did not stop within two seconds, and Dictum does not start a second hook beside it. Click **Pause all hotkeys** and then **Resume hotkeys** in the tray menu to try again. If the message comes back, restart the daemon: quit Dictum from the tray menu and start it again.

On Windows, Dictum keeps its hotkey from reaching the app you type in. On X11 the app also gets the keys, so pick a combination that the app does not use.

### Wayland: the shortcut portal {#shortcuts-portal}

On Wayland, an app cannot see the keys you press in other apps. Dictum asks the desktop's GlobalShortcuts portal for its hotkeys, and the desktop tells it when they are pressed and released, so holding the key to talk works. Your hotkey is a suggestion: the desktop asks you to confirm it once, and may assign another key or none.

- **KDE Plasma 6:** a dialog asks the first time Dictum starts. Accept it. To change or assign the key later, open System Settings → Shortcuts and find Dictum. If you closed the dialog, Dictum says `Binding the shortcuts was cancelled in the desktop dialog.` Quit Dictum and start it again to get the dialog back.
- **GNOME 48 and later:** GNOME has the portal and asks the same way.
- **Older GNOME** (such as GNOME 46 in Ubuntu 24.04) **and compositors without the portal:** the row says `org.freedesktop.portal.GlobalShortcuts is not available`. Bind a key in your desktop's shortcut settings to run `dictum toggle`: press once to start, once more to stop and paste. In GNOME, use Settings → Keyboard → View and Customize Shortcuts → Custom Shortcuts. Give the command with its full path, such as `/home/you/.local/bin/dictum toggle` for the tarball, or `/home/you/Apps/dictum-0.5.1-linux-x64.AppImage dictum toggle` for the AppImage.
- **Sway and other compositors with key bindings:** bind `dictum start` on press and `dictum stop` on release, so holding the key works:

    ```text
    bindsym --no-repeat Ctrl+Alt+space exec dictum start
    bindsym --release Ctrl+Alt+space exec dictum stop
    ```

`No D-Bus session bus: …`
:   Dictum runs outside your desktop session. See [Desktop session](#desktop-session).

The log shows which key the desktop gave each profile, in a line such as `Portal bound 'dictate' to Ctrl+Alt+Space`. `(not set)` in place of the key means the desktop did not say. See [Logs](#logs).

## Pasting {#pasting}

Dictum puts the text on the clipboard, presses Ctrl+V for you, then puts back what the clipboard held. These rows check each step:

| Row | System page | What it checks |
| --- | --- | --- |
| `paste` | **Paste** | Windows: the clipboard and the key presses (`Win32 clipboard + SendInput`). |
| `clipboard` | **Clipboard** | Linux: the clipboard tool. |
| `active window` | **Active window** | X11: `xprop`, which tells Dictum when a terminal has the focus. |
| `paste (portal)` | **Paste (portal)** | Wayland: the RemoteDesktop portal, which presses Ctrl+V. |
| `paste (ydotool)` | **Paste (ydotool)** | Wayland: `ydotoold`, the fallback. |
| `paste order` | **Paste order** | Wayland: the ways Dictum tries, in order, such as `RemoteDesktop portal → ydotool`. |

When pasting fails, the recognised text is still in **History** in the tray menu, unless history is off: copy it from there.

Dictum does not type the text key by key. A profile whose `config.json` still says `"output": "type"`, from an earlier version, pastes, and the [config warnings](#config-warnings) row names it.

### Clipboard tools on Linux {#clipboard-tools}

Dictum uses the clipboard through command-line tools: `wl-clipboard` (`wl-copy`, `wl-paste`) on Wayland, and `xclip` or `xsel` on X11.

`wl-clipboard not found` → `Install wl-clipboard: 'sudo pacman -S wl-clipboard' or 'sudo apt install wl-clipboard'.`
:   Without it, a dictation fails with `wl-clipboard is not installed.`

`xclip not found` → `Install xclip: 'sudo pacman -S xclip' or 'sudo apt install xclip'.`
:   `xsel` works too.

`xprop not found` → `Install xprop (package xorg-xprop / x11-utils) so terminals get Ctrl+Shift+V.`
:   Without it, Dictum pastes with Ctrl+V into terminals too.

Dictum looks for the tools when the daemon starts. After you install one, quit Dictum from the tray menu and start it again.

### Pressing Ctrl+V on Wayland {#wayland-paste}

On Wayland, Dictum presses Ctrl+V through one of three ways, and keeps the first that works:

- **The RemoteDesktop portal.** The desktop asks once whether Dictum may control the keyboard. Allow it. Dictum keeps a token so that it is not asked again: `remote-desktop.token` in `~/.local/share/dictum`. If you denied it, Dictum says `Keyboard control was not allowed in the desktop dialog.` Quit Dictum and start it again to be asked again. Deleting that file, while Dictum is not running, also makes the desktop ask again.
- **ydotool.** It needs `ydotoold` running, with access to `/dev/uinput`. Install it and start the service: `sudo pacman -S ydotool`, then `systemctl --user enable --now ydotool`. The `paste (ydotool)` row warns `ydotoold not running (optional fallback)` until then. That warning is fine while the portal works.
- **wtype**, on Hyprland and Sway, which Dictum tries first there.

To choose one, open **Settings → General → Pasting settings ›** and set **Paste backend on Wayland**. **Automatic** tries them in turn and keeps the first that works: on Sway and Hyprland wtype, then ydotool, then the desktop portal; elsewhere, such as GNOME and KDE Plasma, the desktop portal, then ydotool.

When none works, a dictation fails with `No way to send Ctrl+V; the text is on the clipboard.` Paste it yourself with Ctrl+V.

### Terminals {#terminals}

Terminals paste with Ctrl+Shift+V. On Windows and X11, Dictum sees which app has the focus and uses Ctrl+Shift+V for the apps in **Terminal apps** (**Settings → General → Pasting settings ›**). Add your terminal's process or window class name there if it is missing.

On Wayland, Dictum cannot see which app has the focus. Make a profile for the terminal and set its **Paste shortcut** to Ctrl+Shift+V in **Settings → Profiles**.

### The old text is pasted {#old-text}

Dictum puts back the previous clipboard text 150 ms after it presses Ctrl+V. A slow app may read the clipboard after that, and paste the old text. Raise **Clipboard restore delay, ms** in **Settings → General → Pasting settings ›**, for example to 500. The most it takes is 5000.

## Administrator rights {#administrator}

On Windows, the `elevation` row (**Administrator rights**) shows only when Dictum runs as administrator:

`Running as administrator` → `Dictum needs administrator rights only to paste into apps that run as administrator; if you do not dictate into such apps, start it as a normal user.`
:   Dictum installs for your user and needs no administrator rights for anything else. Unless you dictate into such apps, quit Dictum and start it again the usual way, from the Start menu. **Start Dictum at login** always starts it as a normal user.

Windows lets a program press keys only in programs with the same rights or fewer. Running as administrator, Dictum pastes into every app, those that run as administrator included. Running as a normal user, it cannot paste into an app that runs as administrator, such as a terminal opened with "Run as administrator": nothing is pasted there, and its hotkeys do nothing while such an app has the focus. Copy the text from **History** in the tray menu, and paste it with Ctrl+V yourself.

## API keys and the keyring {#secrets}

A server's API key can be in the config in three ways: `secret:<name>`, a key kept in the system keyring; `env:<VAR>`, read from an environment variable; or the key itself, in plain text, which Dictum warns about. When you type a key in Settings or in the setup wizard, Dictum stores it in the keyring and writes `secret:<name>` in the config.

=== "Windows 11"

    The `secrets` row (**Secret store**) reads `Windows Credential Manager`. Dictum's keys are generic credentials named `dictum:<name>`, under Control Panel → Credential Manager → Windows Credentials.

=== "Linux"

    There is no `secrets` row. A missing or locked keyring shows in the row of the provider or processor that needs the key, and in Settings when you store a key ("The key could not be stored: …"). Dictum uses the Secret Service: GNOME Keyring or KWallet.

These texts mean:

`Secret '<name>' is not in the OS secret store.`
:   The config names a key that is not stored. Type the key again in Settings, or store it with `dictum secret set <name>`. `dictum secret check <name>` tells whether it is there, without printing it.

`Secret '<name>' cannot be read from the OS secret store: …`
:   Credential Manager or the keyring failed to answer, and the rest of the text says why. Until it answers, a processor that needs the key follows **If the server fails**, as when the key is not stored.

`No Secret Service is running (KWallet or GNOME Keyring); use 'env:VAR' for API keys instead.`
:   Install and start GNOME Keyring or KWallet, or put the key in an environment variable and set the field to `env:<VAR>`.

`The Secret Service (KWallet or GNOME Keyring) cannot be reached over the D-Bus session bus: …; use 'env:VAR' for API keys instead.`
:   Dictum runs outside your desktop session, such as over SSH or as a service, where the keyring cannot be reached. Start Dictum from your desktop session (see [Desktop session](#desktop-session)), or put the key in an environment variable and set the field to `env:<VAR>`.

`The keyring prompt was dismissed.`
:   The keyring asked to be unlocked and the dialog was closed. Try again and unlock it.

`Environment variable '<VAR>' is not set.`
:   Set the variable where Dictum starts. The daemon that the desktop app starts gets the desktop app's environment.

`… rejected the API key` → `Check the apiKey reference in the config.`
:   The server got a key and refused it. Check the key with the server's provider, then type it again.

## Desktop session {#desktop-session}

On Linux, the `session` row (**Desktop session**) names the session type and the desktop, such as `Wayland, KDE` or `X11, GNOME`. Dictum chooses its hotkey and paste methods by it.

`No graphical session found (neither WAYLAND_DISPLAY nor DISPLAY is set).`
:   The `platform` row (**Platform**). Dictum runs outside a desktop session: over SSH, on a text console, or as a service that does not get the session's environment. Hotkeys and pasting do not work there. Start Dictum from your desktop session. To start it at login, use **Start Dictum at login** in **Settings → General**, or `dictum daemon --install-autostart`.

### No tray icon {#tray-icon}

The **Tray icon** row on **Settings → System**:

`No tray on this desktop`
:   GNOME shows tray icons only with an extension. Install "AppIndicator and KStatusNotifierItem Support", then start Dictum again.

`Hidden in the overflow area`
:   The icon shows whether Dictum is recording, so keep it visible. On Windows, click **Open tray settings**, then set Other system tray icons → Dictum to On. On KDE, open the System Tray settings, then set Entries → Dictum to Shown.

### No overlay {#overlay}

The **Listening overlay** row on **Settings → System**:

`Needs XWayland`
:   The overlay is an X11 window, and this Wayland session runs without XWayland. Turn XWayland on in your compositor's settings. The tray icon still shows when Dictum records.

`Turned off`
:   Turn it on in **Settings → General → Listening overlay**.

## Daemon {#daemon}

The daemon is the background process that does the work. The desktop app starts it, and starts it again if it stops. The `daemon` row (**Daemon**):

`running (pid 8412), Idle; 1 hotkey(s) registered via …`
:   All is well. When the row is `WARN`, its second line says why the hotkeys do not work; see [Hotkeys](#hotkeys).

`not running` → `Start it with 'dictum daemon', or at login with 'dictum daemon --install-autostart'.`
:   Start the desktop app, which starts the daemon. If you use Dictum without the desktop app, run `dictum daemon`.

`not answering: it took the connection but sent no status within 10 s` → `Stop the dictum daemon process, then start it again with 'dictum daemon'.`
:   The daemon hangs. End the `dictum` process in Task Manager or with `pkill -x dictum`. The desktop app then starts a new one.

`No answer within 15 s`
:   A check took too long. Run doctor again.

On **Settings → System**, the checks run inside the daemon. When it is not running, the page shows one row, **Daemon**: **Not running**, "The checks run inside the daemon; they run again as soon as it is back."

When the daemon failed to start, or came from another version of Dictum after an update, the tray menu shows **Restart daemon**. Click it. `Another Dictum daemon is already running.` means a daemon already runs: quit Dictum from the tray menu before you run `dictum daemon` yourself.

## Speech recognition {#speech-recognition}

The `gpu` row (**GPU**) names the graphics card Whisper can use, or says `none that Whisper can use; speech recognition runs on the CPU`. It is always OK: it is there so you know where recognition runs.

Each speech provider has a row, `stt:<id>` (**Speech provider &lt;id&gt;**). For a local Whisper provider:

`ggml-large-v3-turbo-q8_0.bin, runtime Vulkan on …`
:   All is well: the model, and where it runs.

`Model not found: …` or `VAD model not found: …` → `Run 'dictum models pull …', which downloads …`
:   The model file is missing. The hint names the model's id in Dictum's catalog, such as `vad-silero-6.2` for the voice activity model, and the address the file comes from. Run that command, or download the model in **Settings → Models**. You can also turn **Voice activity detection** off in **Settings → Speech providers**. For a file the catalog does not know, see [Model files](#models).

`… on a software Vulkan device (…)` → `This is slow and can produce wrong text; set "runtime": "cpu".`
:   Whisper found no real graphics card for Vulkan, only a software one. In **Settings → Speech providers**, set **Runtime** to **CPU**, then quit Dictum from the tray menu and start it again: a runtime change applies after the daemon restarts.

`Whisper failed to load: …` → `Try "runtime": "cpu" in the provider config.`
:   Set **Runtime** to **CPU** as above.

`… (configured 'cpu', but this process already uses 'vulkan')`
:   Two Whisper providers ask for different runtimes. The first one loaded decides for the whole daemon. Give both the same **Runtime**.

When `dictum doctor` says `No answer within 15 s` for a local provider, it was still loading the model: the terminal loads it again, while the daemon has it loaded already. Look at **Settings → System**, or run doctor again.

For a remote server:

`http://…/v1 reachable, model …`
:   All is well.

`… rejected the API key (401)` → `Check the apiKey reference in the config.`
:   See [API keys and the keyring](#secrets).

`Secret '<name>' is not in the OS secret store.` → `The API key cannot be read: store it with 'dictum secret set <name>', or type it in Settings → Speech providers.`
:   The provider's key is `secret:<name>`, and the keyring does not have it. Type the key in the provider's **API key (optional)** box, or run the command the hint gives. With `env:<VAR>`, the row says `Environment variable '<VAR>' is not set.`, and the hint names the variable to set. Without a secret store, such as on Linux with no Secret Service, the hint says to give the key as `env:VAR`. See [API keys and the keyring](#secrets).

`http://…/v1: …` → `Is the server running, and is baseUrl correct (including /v1)?`
:   Dictum cannot reach the server. Check that it runs, and that the **URL** ends with the API's version, such as `http://localhost:8080/v1`.

If recognition is slow, pick a smaller model in **Settings → Models**. To see how long a provider takes, click **Test** in **Settings → Speech providers** and speak for five seconds: it shows "Recognised in … ms".

## LLM clean-up {#llm-cleanup}

Each LLM prompt processor has a row, `processor:<id>` (**Processor &lt;id&gt;**):

`http://localhost:8080/v1 reachable, model qwen3-4b, prompt 'cleanup'`
:   The server answers.

`http://…/v1: …` → `Is the LLM server running, and is baseUrl correct (including /v1)?`
:   Dictum cannot reach the server. Check that the server runs, and that its **URL** in **Settings → Processors** ends with `/v1`.

`Secret '<name>' is not in the OS secret store.` → `The API key cannot be read: store it with 'dictum secret set <name>', or type it in Settings → Processors.`
:   The processor's key is `secret:<name>`, and the keyring does not have it. Type the key in the processor's **API key (optional)** box, or run the command the hint gives. With `env:<VAR>`, the row says `Environment variable '<VAR>' is not set.`, and the hint names the variable to set. Without a secret store, such as on Linux with no Secret Service, the hint says to give the key as `env:VAR`. See [API keys and the keyring](#secrets).

    Until the key is there, a dictation follows **If the server fails**: with **Keep the text as recognised**, Dictum pastes the text without this processor's changes and logs `Processor 'cleanup' failed, keeping the input text: LLM 'cleanup' cannot read its API key: …`; with **Fail the dictation**, the dictation fails with that reason.

`… rejected the API key` → `Check the apiKey reference in the config.`
:   See [API keys and the keyring](#secrets).

`http://…/v1/models answered 404`
:   The server answers, but has no list of models. Clean-up may still work; try a dictation.

If the row is OK but the text comes out unchanged, the server did not answer in time, or Dictum rejected the answer. Dictum then pastes the text as recognised and writes the reason to the log:

- `Processor 'cleanup' failed, keeping the input text: …`: an error or a timeout. The default timeout is 4 seconds. Raise **Timeout, seconds** in **Settings → Processors** for a slow server.
- `Processor 'cleanup' answer was cut off at the token limit while the model was still reasoning; …`: the model thinks before it answers. Turn reasoning off on the server, or raise **Max tokens (empty = automatic)**.
- `Processor 'cleanup' answer added line breaks`, `kept 3 of 12 words`, `is in another script`: the model rewrote the text instead of fixing it. Try another model.

To make such a dictation fail instead, set **If the server fails** to **Fail the dictation**. The [LLM clean-up guide](guides/llm-cleanup.md) sets up a server from start to finish.

## Words come out translated {#translated-words}

You say a sentence in English, and Dictum pastes it in Ukrainian, or the other way round. Whisper writes in the language it is told to use: when that is not the language you speak, it translates.

How Dictum chooses the language depends on what you pick under the profile's **Languages** in **Settings → Profiles**:

- **One:** every dictation is in that language. Speech in another language comes out translated.
- **Several:** Whisper chooses from your languages. The *main* language has priority: another one is used only when Whisper is sure of it. A short phrase in your second language may not convince it, and then comes out in the main language.
- **Auto:** Whisper detects the language of each dictation, and can pick a language you do not speak.

To fix it:

1. Make the language you dictate in most the main one: in **Settings → Profiles**, under **Languages**, click **Make main** next to it.
2. If you switch languages often, give each language its own profile with its own hotkey. The [profiles guide](guides/profiles.md) shows how.
3. Keep only the languages you dictate in. Close languages, such as Ukrainian and Russian, confuse Whisper; doctor warns about such pairs.
4. Set the language when you have words in **Your words**: a word list pulls detection towards its own language.

The log shows each choice in a line such as:

```text
Whisper 'local' picked 'uk' from uk, en (detected 'en', p=0.42; the first language has priority, and another one needs p >= 0.50)
```

Here Whisper heard English, but was not sure enough, so Dictum used the main language, Ukrainian. The [languages guide](guides/languages.md) explains the main language in full.

A remote speech server takes one language: with several in a profile, it always gets the first one.

## Windows SmartScreen {#smartscreen}

Dictum's installer is not signed yet. When you open it, Windows SmartScreen says "Windows protected your PC". Click **More info**, then **Run anyway**. The same can happen the first time you start `dictum-gui.exe` from the zip.

Before you click **Run anyway**, check that the file is the one published. Each release on [GitHub](https://github.com/Sigil-build/dictum/releases) has a `SHA256SUMS` file. Download it next to the installer, then run in PowerShell, in that folder:

```powershell
(Get-FileHash .\dictum-0.5.1-win-x64.msi -Algorithm SHA256).Hash.ToLower()
Select-String -Path .\SHA256SUMS -Pattern "dictum-0.5.1-win-x64.msi"
```

Both lines must show the same hash.

**Smart App Control** blocks unsigned apps outright, with no **Run anyway**. Dictum cannot be installed while it is on. You find it in Windows Security → App & browser control → Smart App Control settings.

## Update check {#update-check}

**Settings → About Dictum** says "The last check failed:" and why, when the [update check](reference/settings.md#check-for-updates) got no answer from GitHub. Dictum tries again at the next daily check; **Check now** tries at once. Nothing else is affected.

- "GitHub could not be reached." or "GitHub did not answer within 10 seconds.": there is no network, or a firewall or proxy blocks `api.github.com`. On Windows, Dictum uses the system's proxy settings. On Linux it reads only the `https_proxy` (or `all_proxy`) and `no_proxy` environment variables it was started with, not the proxy set in GNOME's or KDE's settings.
- "GitHub asks to wait before the next check.": GitHub answers 60 requests an hour from one address without a login, and other programs on your network used them up. Wait an hour.
- "GitHub answered" with a number: GitHub had a problem of its own. Try later.

You can always see the releases yourself: **Releases** on the same page opens them on GitHub.

## Logs {#logs}

Dictum writes what it does to log files:

| | Windows | Linux |
| --- | --- | --- |
| Folder | `%LOCALAPPDATA%\Dictum\logs` | `~/.local/share/dictum/logs` |
| The daemon and the command line | `dictum.log` | `dictum.log` |
| The desktop app | `dictum-gui.log` | `dictum-gui.log` |

`dictum-gui.log` also gets the output of a daemon the desktop app started, in lines that begin with `daemon:`. Each line has the time and a level: `DEBUG`, `INFO`, `WARNING` or `ERROR`. When a file passes 1 MB, Dictum renames it to `dictum.log.1` and starts a new one, and keeps three old files.

The log holds the hotkeys, the backends, the timings and the errors. The text you dictate shows only as its length, such as `<42 chars>`.

To read the end of the log:

=== "Windows 11"

    ```powershell
    Get-Content "$env:LOCALAPPDATA\Dictum\logs\dictum.log" -Tail 50
    ```

=== "Linux"

    ```sh
    tail -n 50 ~/.local/share/dictum/logs/dictum.log
    ```

### More detail with --verbose {#verbose}

`dictum daemon --verbose` prints every line to the terminal as it happens, and writes the dictated text itself into the log.

1. Quit Dictum: **Quit Dictum** in the tray menu. Only one daemon can run.
2. In a terminal, run the daemon. On Windows, by its full path:

    ```powershell
    & "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" daemon --verbose
    ```

    On Linux:

    ```sh
    dictum daemon --verbose
    ```

3. Do what goes wrong, and read the lines it prints. If you start the desktop app now, it uses this daemon.
4. Press Ctrl+C to stop it, then start Dictum as usual.

!!! warning
    With `--verbose`, the log file keeps the text you dictated. Remove those lines before you share the log.

When you report a problem, attach the output of `dictum doctor --json` and the log lines from the time it happened.
