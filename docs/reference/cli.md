# Command line

<!-- Generated from the program by CliReferenceTests. Do not edit: run DICTUM_WRITE_DOCS=1 dotnet test --project tests/Dictum.Cli.Tests --filter-class "*.CliReferenceTests" -->

Dictum: voice to text. Results go to stdout; logs and progress go to stderr.

Every command takes `--help` (`-h`) to print its own help, and `dictum --version` prints the version.

## Global options {#global-options}

These options work on every command, before or after the command's name. The commands below list only their own options.

| Option | Description |
| --- | --- |
| `--config` | Config file to use instead of the default location |
| `--verbose`, `-v` | Log details to stderr, including dictated text |

## `dictum init` {#init}

Set Dictum up: choose and download a speech model, cleanup and your words

Usage: `dictum init [options]`

| Option | Description |
| --- | --- |
| `--yes`, `-y` | Do not ask: use the options given and keep or recommend everything else |
| `--language` | Language you dictate in: one code such as uk, several such as "uk, en" to pick from, or auto |
| `--stt` | Speech model id (see 'dictum models list') |
| `--llm` | Text cleanup: none, or the base URL of an OpenAI-compatible server, e.g. http://192.168.1.10:8080/v1 |
| `--llm-model` | Model name on that server |
| `--llm-api-key` | API key for that server: env:VAR, secret:name, or the key itself (then kept in the OS secret store) |
| `--vocab-file` | Words to add to the profile, one per line |
| `--autostart` | Start the daemon at login |
| `--gpu` | auto (default) or cpu<br>Values: auto, cpu |
| `--json` | Print a JSON summary |

## `dictum transcribe` {#transcribe}

Transcribe a WAV file (any rate or channel count)

Usage: `dictum transcribe <file> [options]`

| Argument | Description |
| --- | --- |
| `<file>` | WAV file to transcribe, or - for stdin |

| Option | Description |
| --- | --- |
| `--profile`, `-p` | Profile id from the config (default: the first profile) |
| `--stt` | STT provider id from the config |
| `--model`, `-m` | Whisper model file (name in the models directory, or a path); needs no config |
| `--lang`, `-l` | Language code such as uk, a list such as "uk, en" to pick from, or auto |
| `--pipeline` | Comma-separated processor ids, replacing the profile's chain |
| `--raw` | Skip all processing |
| `--json` | Print a JSON object with text, language, provider and timings |

## `dictum record` {#record}

Record from the microphone, then transcribe. Enter or Ctrl+C stops, Esc cancels.

Usage: `dictum record [options]`

| Option | Description |
| --- | --- |
| `--duration` | Stop after this long, e.g. 30s |
| `--until-silence` | Stop after this much silence following speech, e.g. 2s |
| `--device`, `-d` | Microphone name or id from 'dictum devices' |
| `--out` | Where the text goes: stdout, clipboard, or paste into the focused app<br>Default: stdout<br>Values: clipboard, paste, stdout |
| `--dump-frames` | Write level and spectrum frames to a JSON file (for the overlay prototype) |
| `--save-audio` | Also save the recording as a 16 kHz WAV file, e.g. to check the microphone |
| `--profile`, `-p` | Profile id from the config (default: the first profile) |
| `--stt` | STT provider id from the config |
| `--model`, `-m` | Whisper model file (name in the models directory, or a path); needs no config |
| `--lang`, `-l` | Language code such as uk, a list such as "uk, en" to pick from, or auto |
| `--pipeline` | Comma-separated processor ids, replacing the profile's chain |
| `--raw` | Skip all processing |
| `--json` | Print a JSON object with text, language, provider and timings |

## `dictum process` {#process}

Run text through processors, e.g. dictum transcribe memo.wav --raw \| dictum process --pipeline cleanup

Usage: `dictum process [<text>] [options]`

| Argument | Description |
| --- | --- |
| `<text>` | Text to process, or - (or nothing) to read stdin |

| Option | Description |
| --- | --- |
| `--pipeline` | Comma-separated processor ids (default: the profile's chain) |
| `--profile`, `-p` | Profile whose chain and language to use |
| `--lang`, `-l` | Language of the text, passed to LLM prompts |
| `--json` | Print a JSON object with the text and per-processor timings |

## `dictum stt` {#stt}

List and test speech-to-text providers

Usage: `dictum stt <command>`

| Command | Description |
| --- | --- |
| [`list`](#stt-list) | List configured STT providers |
| [`test`](#stt-test) | Check a provider and transcribe a sample with it |

## `dictum stt list` {#stt-list}

List configured STT providers

Usage: `dictum stt list`

## `dictum stt test` {#stt-test}

Check a provider and transcribe a sample with it

Usage: `dictum stt test <id> [options]`

| Argument | Description |
| --- | --- |
| `<id>` | Provider id |

| Option | Description |
| --- | --- |
| `--file`, `-f` | WAV file to transcribe instead of recording 5 seconds |
| `--lang`, `-l` | Language code, a list such as "uk, en" to pick from, or auto |

## `dictum profiles` {#profiles}

List and inspect profiles

Usage: `dictum profiles <command>`

| Command | Description |
| --- | --- |
| [`list`](#profiles-list) | List profiles |
| [`set-hotkey`](#profiles-set-hotkey) | Change a profile's hotkey; a running daemon picks it up at once |
| [`show`](#profiles-show) | Show a profile with its provider and processors |

## `dictum profiles list` {#profiles-list}

List profiles

Usage: `dictum profiles list`

## `dictum profiles set-hotkey` {#profiles-set-hotkey}

Change a profile's hotkey; a running daemon picks it up at once

Usage: `dictum profiles set-hotkey <id> <hotkey>`

| Argument | Description |
| --- | --- |
| `<id>` | Profile id |
| `<hotkey>` | Combination, e.g. "Ctrl+Alt+Space" or "Ctrl+Alt+`" |

## `dictum profiles show` {#profiles-show}

Show a profile with its provider and processors

Usage: `dictum profiles show <id>`

| Argument | Description |
| --- | --- |
| `<id>` | Profile id |

## `dictum vocab` {#vocab}

Words, names and terms the recognizer should spell exactly, per profile

Usage: `dictum vocab <command> [options]`

| Option | Description |
| --- | --- |
| `--profile`, `-p` | Profile id (default: the first profile) |

`--profile` also works on every subcommand of `dictum vocab`.

| Command | Description |
| --- | --- |
| [`list`](#vocab-list) | Print the profile's words, one per line |
| [`add`](#vocab-add) | Add words to the end of the list |
| [`remove`](#vocab-remove) | Remove words from the list |
| [`clear`](#vocab-clear) | Remove every word from the list |
| [`import`](#vocab-import) | Add words from a file |

## `dictum vocab list` {#vocab-list}

Print the profile's words, one per line

Usage: `dictum vocab list`

## `dictum vocab add` {#vocab-add}

Add words to the end of the list

Usage: `dictum vocab add <terms>...`

| Argument | Description |
| --- | --- |
| `<terms>` | Words or phrases; quote phrases with spaces |

## `dictum vocab remove` {#vocab-remove}

Remove words from the list

Usage: `dictum vocab remove <terms>...`

| Argument | Description |
| --- | --- |
| `<terms>` | Words or phrases to remove (any letter case) |

## `dictum vocab clear` {#vocab-clear}

Remove every word from the list

Usage: `dictum vocab clear`

## `dictum vocab import` {#vocab-import}

Add words from a file

Usage: `dictum vocab import <file>`

| Argument | Description |
| --- | --- |
| `<file>` | Text file with one word or phrase per line ('#' starts a comment), or - for stdin |

## `dictum models` {#models}

List and download the speech models Dictum knows about

Usage: `dictum models <command>`

| Command | Description |
| --- | --- |
| [`list`](#models-list) | Show catalog models, whether they are installed, and other model files in the models directory |
| [`pull`](#models-pull) | Download models into the models directory (resumes after interruptions, checks SHA-256) |

## `dictum models list` {#models-list}

Show catalog models, whether they are installed, and other model files in the models directory

Usage: `dictum models list [options]`

| Option | Description |
| --- | --- |
| `--json` | Print JSON |
| `--all` | Include models meant for tests |

## `dictum models pull` {#models-pull}

Download models into the models directory (resumes after interruptions, checks SHA-256)

Usage: `dictum models pull <ids>...`

| Argument | Description |
| --- | --- |
| `<ids>` | Catalog ids, e.g. whisper-small-q8 |

## `dictum devices` {#devices}

List microphones

Usage: `dictum devices [options]`

| Option | Description |
| --- | --- |
| `--json` | Print JSON |

## `dictum config` {#config}

Create, locate, validate and edit the config file

Usage: `dictum config <command>`

| Command | Description |
| --- | --- |
| [`path`](#config-path) | Print the config file path |
| [`validate`](#config-validate) | Check the config for errors |
| [`edit`](#config-edit) | Open the config in $VISUAL / $EDITOR (or the system editor) |

## `dictum config path` {#config-path}

Print the config file path

Usage: `dictum config path`

## `dictum config validate` {#config-validate}

Check the config for errors

Usage: `dictum config validate`

## `dictum config edit` {#config-edit}

Open the config in $VISUAL / $EDITOR (or the system editor)

Usage: `dictum config edit`

## `dictum history` {#history}

Show recent transcriptions

Usage: `dictum history [options]`

| Option | Description |
| --- | --- |
| `--last`, `-n` | How many entries<br>Default: 10 |
| `--json` | Print JSON |

## `dictum doctor` {#doctor}

Check config, microphone and every provider, with hints for anything broken

Usage: `dictum doctor [options]`

| Option | Description |
| --- | --- |
| `--json` | Print JSON |

## `dictum daemon` {#daemon}

Run in the background: hotkeys, dictation and the control socket (Ctrl+C stops)

Usage: `dictum daemon [options]`

| Option | Description |
| --- | --- |
| `--install-autostart` | Start the daemon at login, then exit |
| `--uninstall-autostart` | Stop starting the daemon at login, then exit |

## `dictum toggle` {#toggle}

Start dictation, or stop it and paste the text (bind this in your compositor)

Usage: `dictum toggle [options]`

| Option | Description |
| --- | --- |
| `--profile`, `-p` | Profile id (default: the active or first profile) |

## `dictum start` {#start}

Start dictation

Usage: `dictum start [options]`

| Option | Description |
| --- | --- |
| `--profile`, `-p` | Profile id (default: the active or first profile) |

## `dictum stop` {#stop}

Stop dictation and paste the text

Usage: `dictum stop [options]`

| Option | Description |
| --- | --- |
| `--profile`, `-p` | Profile id (default: the active or first profile) |

## `dictum cancel` {#cancel}

Stop dictation and discard it

Usage: `dictum cancel [options]`

| Option | Description |
| --- | --- |
| `--profile`, `-p` | Profile id (default: the active or first profile) |

## `dictum reload` {#reload}

Re-read the config file now

Usage: `dictum reload [options]`

| Option | Description |
| --- | --- |
| `--profile`, `-p` | Profile id (default: the active or first profile) |

## `dictum shutdown` {#shutdown}

Stop the running daemon

Usage: `dictum shutdown [options]`

| Option | Description |
| --- | --- |
| `--profile`, `-p` | Profile id (default: the active or first profile) |

## `dictum status` {#status}

Show whether the daemon runs, its state, hotkeys and backends

Usage: `dictum status [options]`

| Option | Description |
| --- | --- |
| `--json` | Print JSON |
| `--watch` | Keep printing state changes, results and status as JSON lines until Ctrl+C |
| `--frames` | With --watch, also print audio frames (50 per second) |

## `dictum secret` {#secret}

Store API keys in the OS secret store, for "apiKey": "secret:&lt;name&gt;"

Usage: `dictum secret <command>`

| Command | Description |
| --- | --- |
| [`set`](#secret-set) | Store a secret; the value is read from stdin or typed without echo |
| [`check`](#secret-check) | Tell whether a secret is stored, without printing it |
| [`delete`](#secret-delete) | Remove a stored secret |

## `dictum secret set` {#secret-set}

Store a secret; the value is read from stdin or typed without echo

Usage: `dictum secret set <name>`

| Argument | Description |
| --- | --- |
| `<name>` | Secret name, e.g. groq |

## `dictum secret check` {#secret-check}

Tell whether a secret is stored, without printing it

Usage: `dictum secret check <name>`

| Argument | Description |
| --- | --- |
| `<name>` | Secret name |

## `dictum secret delete` {#secret-delete}

Remove a stored secret

Usage: `dictum secret delete <name>`

| Argument | Description |
| --- | --- |
| `<name>` | Secret name |
