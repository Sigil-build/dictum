# Settings

Settings is the desktop app's window for everything Dictum does. To open it, click the Dictum icon in the tray, or choose **Settings** in the icon's menu. **History** in the same menu opens it at the History page. Starting the desktop app while it already runs opens Settings too.

If Dictum is not set up yet, each of these opens the [setup wizard](setup-wizard.md) instead.

## How changes are saved {#saving}

- There is no Save button. Settings writes `config.json` half a second after your last change.
- The daemon reads the file again as soon as it changes, so a change applies from the next dictation. A change you make while you dictate waits until that dictation ends.
- A field with a value Dictum cannot use turns red and says why. Nothing is saved until you fix it, and a banner at the top says "Changes are not saved until the fields marked in red are fixed."
- If `config.json` cannot be read, for example after a hand edit broke it, the pages are greyed out and the banner says what is wrong. See [Troubleshooting](../troubleshooting.md#config).
- Two settings need a restart, and say so under the field: **UI language** and a local Whisper provider's **Runtime**.

The column on the left lists the pages. The version of Dictum is at its bottom.

## The pages {#pages}

### General {#general}

#### Start Dictum at login {#start-at-login}

Starts the desktop app, with its tray icon, each time you log in to your computer. No window opens. On Windows 11 this is the value `Dictum` under the registry key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; on Linux it is the file `~/.config/autostart/dictum.desktop`. The switch changes it at once; it is not part of `config.json`. Dictum keeps one login item, so turning this on replaces one that starts the daemon alone. See [Start at login](../guides/start-at-login.md).

#### Microphone {#microphone}

The microphone Dictum records from. The first entry follows the system's default input and names it, for example "Default — Headset Microphone"; pick a device to stay on it even when the default changes. A device you chose that is not plugged in stays in the list, marked "(not connected)". While this page is open, the bar under the list shows the input level: speak and watch it. Green is good; red means the input is so loud that it clips. The bar needs the daemon to run.

#### Sounds {#sounds}

A short tick when recording starts and another when it stops, so you know Dictum heard the hotkey without looking at the screen. **Preview** plays both. On by default.

#### History {#history-size}

How many recent dictations Dictum keeps on this computer, from 0 to 5000; 200 by default. It keeps the text only, never the audio, in `history.jsonl` (see [Files](files.md)). 0 turns history off: new dictations are not written, and the [History](#history) page says so. With a lower number, the oldest entries go when the next dictation is saved.

#### UI language {#ui-language}

The language of the desktop app: **Same as the system**, **English** or **Українська**. It applies the next time the desktop app starts: choose **Quit Dictum** in the tray menu, then start Dictum again. The command line is in English either way.

#### Listening overlay {#overlay-settings}

**Overlay settings ›** opens the overlay's own page: whether it shows, where, how big, how opaque and in which colours. See [Listening overlay](#overlay) below.

#### Pasting {#pasting-settings}

**Pasting settings ›** opens the Pasting page: the clipboard restore delay, the terminal apps and, on Linux, how the paste reaches Wayland apps. See [Pasting](#pasting) below.

#### Setup wizard {#setup-wizard}

**Run setup again** opens the [setup wizard](setup-wizard.md) with your current choices filled in. Use it to change the languages, the speech model or the clean-up server in one go. It changes only the first profile, the local Whisper provider that profile uses and the `cleanup` processor; other profiles, providers and processors stay as they are.

### Listening overlay {#overlay}

The overlay is the small animation that shows while you dictate; [Tray icon and overlay](tray-and-overlay.md#overlay) describes what it shows. Open this page from General with **Overlay settings ›**, and go back with the link above its title. The **Preview** at the bottom plays the overlay with your choices. Changes apply at once, even to an overlay on screen.

#### Show the overlay while you dictate {#overlay-enabled}

Turn it off if the tray icon and the sounds are enough for you. On by default.

#### Position {#overlay-position}

**Bottom of the screen** (the default) or **Top of the screen**. The overlay appears on the screen with the mouse pointer, centred, just above the taskbar or panel at the bottom, or just below one at the top.

#### Size {#overlay-size}

**Small**, **Medium** (the default) or **Large**: 80 %, 100 % or 125 % of the normal size. The screen's own scaling applies on top.

#### Opacity {#overlay-opacity}

How opaque the overlay is, from 30 % to 100 % in steps of 5; 100 % by default. Lower it if the overlay covers text you read while you dictate.

#### Colour theme {#overlay-palette}

**Aurora** (blue to violet, the default), **Ember** (orange to pink) or **Mist (neutral)** (light grey). In a high-contrast theme the overlay uses Mist, whatever you pick here.

### Pasting {#pasting}

For profiles whose **Output** is **Paste into the focused app**, Dictum puts the text on the clipboard, presses the paste shortcut for you, then puts back the text the clipboard held before, unless something else changed the clipboard in the meantime. This page tunes that. Open it from General with **Pasting settings ›**.

#### Clipboard restore delay, ms {#paste-delay}

How long Dictum waits after the paste shortcut before it puts the previous clipboard text back: 0 to 5000 ms, 150 by default. Some apps read the clipboard a moment after the keystroke. If one of them pastes your old clipboard instead of the dictation, raise this, for example to 300 or 500.

#### Terminal apps {#terminal-apps}

Apps that paste with Ctrl+Shift+V instead of Ctrl+V: process names on Windows (such as `windowsterminal`), window classes on X11 (such as `konsole`). When a profile's **Paste shortcut** is **Automatic**, Dictum compares the focused app with this list. The list starts with common Linux terminals: `konsole`, `gnome-terminal-server`, `kitty`, `alacritty`, `wezterm-gui`, `foot`, `xterm`, `tilix`, `terminator`, `ghostty`, `xfce4-terminal` and a few more. Type a name and press Enter to add it, or paste several; click × on a name to remove it. On Wayland Dictum cannot see the focused app, so the list does nothing there: in a profile you use for terminals, set **Paste shortcut** to Ctrl+Shift+V.

#### Paste backend on Wayland {#paste-backend}

Linux only; Windows does not show this row. How Dictum presses the paste shortcut in a Wayland session:

- **Automatic** (the default) tries the backends in turn and keeps the first one that works: on Sway and Hyprland wtype, then ydotool, then the desktop portal; elsewhere, such as GNOME and KDE Plasma, the desktop portal, then ydotool.
- **Desktop portal** asks once whether Dictum may control the keyboard, then remembers your answer.
- **ydotool** needs the `ydotoold` service running.
- **wtype** needs the `wtype` program and a compositor that supports it, such as Sway or Hyprland.

Pick one yourself when **Automatic** picks one that does not work for you; the **Paste order** row on the [System](#system) page shows the order in use. See [Troubleshooting](../troubleshooting.md#pasting).

### Profiles {#profiles}

A profile is one way to dictate: its own hotkey, languages, words and processing. The list on the left shows each profile with its languages and hotkey; select one to edit it on the right. **+ New profile** adds a profile with the selected one's languages and speech provider and no hotkey. **Delete profile** removes the selected one; it is off while only one profile is left. See [Profiles](../guides/profiles.md).

#### Name {#profile-name}

What Settings calls the profile. Leave it empty to use its id, such as `dictate`, which is what the tray menu and the tooltip show.

#### Hotkey {#hotkey}

The key combination that starts a dictation with this profile. Click the box, then press the keys: Ctrl, Alt or Win with a key, such as Ctrl+Alt+Space. F13–F24, Pause and Scroll Lock may stand alone. Esc cancels, and Backspace removes the hotkey. While the box listens, Dictum's hotkeys are paused, so pressing a current hotkey does not start a dictation. Two profiles cannot share a hotkey. If another app already uses the combination, a warning says the daemon could not register it; pick another one. On Wayland the desktop may ask you to confirm the shortcut. See [Troubleshooting](../troubleshooting.md#hotkeys).

#### Mode {#hotkey-mode}

**Push to talk** (the default): hold the hotkey while you speak, let go to paste. **Toggle**: press it once to start and once more to stop. Toggle suits long dictations.

#### Languages {#languages}

The languages you speak with this profile:

- **One**: Whisper always writes this language. The most accurate choice when you speak one language.
- **Several**: pick each language with **Add a language**. The first is the main language, marked "main"; **Make main** moves another one first. The main language has priority: Whisper uses another one only when it is sure of it.
- **Auto**: Whisper detects the language of each dictation, from any of its 99. Slower, and less reliable for short or mixed speech.

Some languages are so close that Whisper often confuses them, such as Ukrainian and Russian. With two of them in one list, a warning suggests keeping one here and moving the other to its own profile. A remote speech server gets only the first language of a list. See [Languages](../guides/languages.md).

#### Your words {#words}

Names and terms the recogniser gets wrong, spelled the way you want them, such as Nexus5 or Avalonia. Type one and press Enter, or paste a list, one per line or comma-separated; click × on a word to remove it. Dictum gives the list to Whisper as a hint, and to the profile's LLM prompts. The hint holds 400 characters together with the **Speech hint**; words beyond that are left out, and a warning names them. With **Auto** languages, a word list can pull detection towards its own language, so set the profile's languages too. See [Your words and replace rules](../guides/words-and-rules.md).

#### Speech hint {#speech-hint}

Extra text Whisper sees before your words, for example a sentence in the style you dictate. Most people leave it empty; use **Your words** for names and terms.

#### Processing chain {#processing-chain}

The processors that work on the text after recognition, in order, such as an LLM prompt that fixes punctuation and replace rules that fix words. **Add a step** offers the processors not in the chain yet; you make them on the [Processors](#processors) page. Drag a step to reorder it, or select it and press Alt+↑ or Alt+↓; click × to take it out of this chain. With no steps, the text is used as recognised.

#### Output {#output}

What Dictum does with the text:

- **Paste into the focused app** (the default): the text appears where your cursor is. The [Pasting](#pasting) page tunes how.
- **Type it, key by key**: not available in Dictum 0.4. A profile set to it fails each dictation with "Typing text character by character is not supported yet."
- **Copy to clipboard only**: the text goes to the clipboard and you paste it yourself. Use it for apps that refuse a simulated paste.

#### Paste shortcut {#paste-shortcut}

The keys Dictum presses to paste: **Automatic** (the default), Ctrl+V or Ctrl+Shift+V. **Automatic** presses Ctrl+Shift+V in the [terminal apps](#terminal-apps) and Ctrl+V elsewhere. On Wayland Dictum cannot tell which app has the focus, so **Automatic** always presses Ctrl+V there; set Ctrl+Shift+V in a profile you use for terminals. Shown only with **Paste into the focused app**.

#### Speech provider {#speech-provider}

Which speech recogniser this profile uses, from the [Speech providers](#speech-recognition) page: a local Whisper with its model, or a remote server.

### Speech providers {#speech-recognition}

A card for each speech recogniser that profiles can use. Each card says which profiles use it. **+ Local Whisper** adds a Whisper that runs on your computer, with voice activity detection on. **+ Remote server** adds a server with an OpenAI-compatible transcription API, such as whisper-server behind llama-swap, speaches, OpenAI or Groq. **Remove provider** is on only while no profile uses the provider; it also deletes the provider's stored API key.

#### Model {#whisper-model}

Local Whisper. The speech model file: the catalog's models you have downloaded, and any other `.bin` file in the models folder. Download models on the [Models](#models) page. A model the provider names but that is missing stays in the list, marked "(not downloaded)".

#### Runtime {#runtime}

Local Whisper. Where Whisper runs: **Automatic** (the default) uses the graphics card through Vulkan when it can, otherwise the processor; **CPU** always uses the processor; **Vulkan** always uses the graphics card. Pick **CPU** if recognition fails or the computer struggles on the graphics card. A change applies after the daemon restarts: choose **Quit Dictum** in the tray menu, then start Dictum again.

#### Threads (0 = automatic) {#threads}

Local Whisper. How many processor threads Whisper uses, from 0 to 64. 0, the default, picks a number from your processor's cores. Lower it to leave cores free for other work.

#### Voice activity detection {#vad}

Local Whisper. Skips the silence before Whisper hears the recording, so pauses do not turn into made-up words. It needs the small Silero model; when that file is missing, a warning gives the command that fetches it: `dictum models pull vad-silero-6.2`. On for new providers and after the setup wizard.

#### URL {#server-url}

Remote server. The server's address, with the API version, such as `http://localhost:8080/v1`.

#### Model {#server-model}

Remote server. The model's name on that server; `whisper-1` for a new provider, which is what OpenAI calls Whisper.

#### API key (optional) {#server-api-key}

Remote server. Leave it empty for a server on your computer. A key you type goes to the system keyring when you press Enter or leave the field: Windows Credential Manager on Windows, the Secret Service on Linux. `config.json` then refers to it as `secret:dictum-stt-<id>` and never holds the key itself. You can also type `env:NAME` to read the key from the environment variable `NAME`. The field never shows a stored key; the line under it says where the key is, and **Remove key** deletes it.

#### Timeout, seconds {#server-timeout}

Remote server. How long Dictum waits for the server's answer, from 1 to 600 seconds; 60 for a new provider. Raise it for a slow server or long dictations.

#### Test {#provider-test}

**Test** records 5 seconds from your microphone, with a level bar, and shows what the provider recognised and how long it took. **Test with a sample** sends a built-in recording instead. Both save your pending changes first and need the daemon to run. Fix any field marked in red before you test.

### Processors {#processors}

Processors work on the text after recognition, in the order a profile's **Processing chain** gives. The list on the left shows each processor with its kind; select one to edit it on the right. There are two kinds:

- **+ LLM prompt** adds a processor that sends the text to a language model on a server you run or use, such as llama-server or Ollama, with a prompt that says what to do with it. It starts with the URL, model and key of your first LLM processor, or `http://localhost:8080/v1` and the model `local`, and with its own copy of the built-in `cleanup` prompt. See [LLM clean-up](../guides/llm-cleanup.md).
- **+ Replace rules** adds a list of find-and-replace rules that runs on your computer. See [Your words and replace rules](../guides/words-and-rules.md).

A new processor joins every profile whose chain is empty. A processor that no profile's chain runs is marked "In no profile" in the list, and its page says that dictation never uses it; **Add to profile…** puts it at the end of a profile's chain. **Remove processor** is on only while no profile's chain uses it; it also deletes the processor's stored API key.

#### URL {#llm-url}

LLM prompt. The server's address, with the API version, such as `http://localhost:8080/v1`. Any server with an OpenAI-compatible chat completions API works.

#### Model {#llm-model}

LLM prompt. The model's name on that server. llama-server answers to any name.

#### API key (optional) {#llm-api-key}

LLM prompt. As for a [remote speech server](#server-api-key): leave it empty for a server on your computer; a typed key goes to the system keyring as `secret:dictum-processor-<id>`, or type `env:NAME`.

#### Timeout, seconds {#llm-timeout}

LLM prompt. How long Dictum waits for the answer before **If the server fails** applies, from 1 to 600 seconds. A new processor starts at 4; the setup wizard gives its `cleanup` processor 15. Raise it for a slow model.

#### Temperature {#temperature}

LLM prompt. How freely the model chooses its words, from 0 to 2; 0.2 by default. Keep it low for clean-up, so the model sticks to your text.

#### Max tokens (empty = automatic) {#max-tokens}

LLM prompt. The longest answer the model may give, in tokens. Empty, the default, scales it to the dictation, up to 2048. Raise it for a model that reasons before it answers, or turn reasoning off on the server: an answer cut off at the limit is thrown away, and the text goes on as it was before this step.

#### If the server fails {#on-error}

LLM prompt. What happens when the server cannot be reached, answers with an error or takes longer than the timeout: **Keep the text as recognised** (the default) pastes the text without this step, and **Fail the dictation** pastes nothing.

#### Wording {#wording}

LLM prompt. **Keep the wording** makes Dictum reject an answer that adds line breaks, keeps under a third of the words or switches to another alphabet; the text then goes on as it was before this step. **Allow rewording** accepts such answers, for prompts that rewrite on purpose, such as `email`. **Automatic** (the default) keeps the wording for the built-in `cleanup` prompt only.

#### Prompt {#prompt}

LLM prompt. The instructions the model gets with your text. Pick a prompt from the list: the built-in `cleanup`, `code-comment` and `email`, and your own. Edit the text below it; Dictum saves it as `prompts/<name>.md` next to `config.json` half a second after you stop typing. Editing a built-in prompt saves your version, which then replaces it; **Restore the built-in prompt** goes back to the original. To start a new prompt from the text on screen, type a **New prompt name** and click **Save as a new prompt**. If other processors use the same prompt, a note names them: your edit changes their prompt too.

#### Preview {#llm-preview}

LLM prompt. Runs this processor on a **Sample sentence**, in English or Ukrainian, and shows the answer and how long it took. It runs by itself after each change while the page is open, and **Run now** runs it again. The preview sends the sample to the server, never your dictations; edit the sample to try your own text.

#### Find {#rule-find}

Replace rules. What to look for. The rules run in order, each on the result of the one before. Type in the empty row at the end to add a rule; click × to remove one.

#### Regex {#rule-regex}

Replace rules. Ticked, **Find** is a .NET regular expression, and **Replace** may use its groups, such as `$1`. A pattern that takes longer than 200 ms on a dictation is abandoned.

#### Replace {#rule-replace}

Replace rules. The text that takes the place of what was found. Leave it empty to delete what was found.

#### Match case {#rule-match-case}

Replace rules. Ticked, the rule finds only the same capital and small letters. Off by default. It applies to plain text only: a regex always matches case exactly, unless it starts with `(?i)`.

#### Whole word {#rule-whole-word}

Replace rules. Ticked, the rule finds whole words only, so a rule for "main" leaves "mainframe" alone. Not for regex rules, which write their own word boundaries with `\b`.

#### On {#rule-on}

Replace rules. Untick it to keep a rule in the list without running it.

#### Import from CSV… {#rules-import}

Replace rules. Adds rules from a CSV file to the end of the list. The columns are Find, Replace, then optionally Match case and Whole word, which take `yes`, `true`, `1`, `x` or `+`. Commas or semicolons separate the cells, as Excel writes them. A first row that starts with "Find" is skipped, and so is a row Dictum cannot read; the line under the button says how many rules came in and which rows were skipped.

### Models {#models}

The speech models in Dictum's catalog, with the space they use on disk and the free space at the top. The line under the title names your graphics card and memory; the recommendation follows from it. Each card shows:

- badges: a recommendation for your graphics card or computer; the state, such as "Installed", "Downloading" or "Not installed"; and a speed class, **Fast**, **Usable** or **Too slow here**;
- the size, the licence, where the file comes from and the languages;
- how long the model takes for 5 seconds of speech: from your own dictations, from a measurement, or estimated from your hardware;
- which profiles use the model.

A model that is not installed has a **Download** button with its size, such as **Download · 264 MB**, or **Download anyway** when it would be too slow on this computer. Downloads go on in the background when you close Settings, continue after a restart, and are checked against the catalog's SHA-256. A failed download offers **Try again**. **Delete**, with the space it frees, removes an installed file; it is off while a profile uses the model.

#### Use in profile… {#use-in-profile}

Makes a profile's local Whisper provider use this model. If other profiles share that provider, the entry says so, such as "Work (also Home)", and they change too.

#### Verify {#verify}

Checks the installed file against the catalog's SHA-256. If it does not match, delete the file and download it again.

#### Measure speed {#measure-speed}

Times the model on a built-in recording, twice, on the provider of a profile that uses it, and keeps the faster run. It shows until your own dictations have timed the model, which count for more.

#### Pause {#pause-download}

Pauses the downloads; **Resume** continues them where they stopped.

#### Cancel {#cancel-download}

Stops this model's download and drops the part already fetched.

### System {#system}

The state of Dictum on this computer. The header names your desktop and when the checks last ran. The card at the top shows whether the desktop app is connected to the daemon, the profiles and their hotkeys, and the paths of the config file and the log; **Restart daemon** shows when the daemon is not running. Under it, a summary counts the problems and warnings, then one row per check, each with a badge. Some rows have a button that fixes the problem.

The checks are the same as `dictum doctor` runs, plus two rows of the desktop app. Which rows you see depends on your system:

| Row | What it checks | If it is not OK |
| --- | --- | --- |
| Config file | `config.json` exists and is valid | [Config](../troubleshooting.md#config) |
| Config warnings | Settings Dictum can use but that look wrong | [Config warnings](../troubleshooting.md#config-warnings) |
| Model files | The model files the providers name are in place | [Models](../troubleshooting.md#models) |
| GPU | The graphics card Whisper can use, if any | [Speech recognition](../troubleshooting.md#speech-recognition) |
| Audio library, Microphone | Dictum can record, and from which microphone | [Microphone](../troubleshooting.md#microphone) |
| Global hotkey | How the hotkeys reach Dictum | [Hotkeys](../troubleshooting.md#hotkeys) |
| Paste, Paste (portal), Paste (ydotool), Paste order, Clipboard, Active window | How the text reaches other apps | [Pasting](../troubleshooting.md#pasting) |
| Administrator rights | Windows: Dictum runs as administrator | [Administrator](../troubleshooting.md#administrator) |
| Secret store | Where API keys are kept | [Secrets](../troubleshooting.md#secrets) |
| Desktop session, Platform | Linux: the desktop session Dictum found, or why it cannot work in it | [Desktop session](../troubleshooting.md#desktop-session) |
| Daemon | The daemon runs and answers | [Daemon](../troubleshooting.md#daemon) |
| Speech provider *id* | The provider can recognise speech | [Speech recognition](../troubleshooting.md#speech-recognition) |
| Processor *id* | The LLM server answers | [LLM clean-up](../troubleshooting.md#llm-cleanup) |
| Listening overlay | The overlay can show on this desktop | [Listening overlay](#overlay) |
| Tray icon | The icon is in the tray and visible | [Tray icon and overlay](tray-and-overlay.md#keep-visible) |

The checks run each time the page opens.

#### Check again {#check-again}

Runs the checks again, for example after you fixed something. The checks run inside the daemon; if it is not running, the Daemon row says so, and the checks run again as soon as it is back.

### History {#history}

Your recent dictations, newest first, as many as **History** on the [General](#general) page keeps. Each entry shows when it was made, the profile, the language Whisper picked, how long the recording was and the first line of the text. A failed dictation says why. The page reads the history file each time it opens.

#### Search dictations {#search}

Shows only the dictations whose text, recognised text or profile contains what you type. Capital and small letters count the same.

#### Copy {#copy}

Puts the selected dictation's final text on the clipboard.

#### Delete {#delete}

Removes the selected dictation from the history file.

#### Recognised {#recognised}

The text as Whisper recognised it, before the profile's processors.

#### Final text {#final-text}

The text Dictum pasted or copied, after the processors.

#### Re-run through… {#re-run}

Runs the recognised text through another profile's processing chain, for example to try a new prompt on a real dictation. The result shows under **Re-run result**, with how long it took. Nothing is pasted, and the history stays as it was. Only profiles with processors are offered.

#### Copy result {#copy-result}

Puts the re-run result on the clipboard.
