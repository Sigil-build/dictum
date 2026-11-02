# Write a plugin

A plugin is a speech engine that runs as a program of its own, next to Dictum. Dictum starts it, sends it each recording over HTTP on your computer, and pastes the text it answers. The program can be written in any language and run any engine, as long as it follows protocol 1, which this page describes.

This page is for people who write a plugin. If someone gave you one, read [Add it to Dictum](#add) and [Use it in a profile](#use).

## What a plugin is {#what}

A plugin is a folder with two things in it:

- `plugin.json`, which says who the plugin is and which program to run on each system;
- the program, or a script that starts it, for Windows, for Linux, or for both.

Dictum runs the program as a child process, in the plugin's folder and with your rights. The program loads a speech model and answers HTTP requests from Dictum, on an address of your computer such as `127.0.0.1`. It uses none of Dictum's code, so you can write it in any language.

When it runs:

- The daemon starts the plugin when a profile uses it: when the daemon starts, and when you save settings that need it. It keeps the plugin running, with its model loaded, while a profile uses it. Saving settings that keep the same plugin, folder, version, model and threads keeps the same process.
- A `dictum` command that needs the plugin, such as `dictum transcribe memo.wav --stt hello`, starts a copy of its own and stops it when the command ends.
- If the plugin exits when nobody asked it to, Dictum starts it again a second later, up to three times within five minutes. After that, the plugin stays down until you save the settings, run `dictum reload`, or restart the daemon.

Whisper is built into Dictum and is not a plugin program. Dictum's own plugins arrive in a later version. This page is about the plugins you write or add yourself.

## plugin.json {#manifest}

`plugin.json` sits at the top of the plugin's folder:

```json
{
  "protocol": 1,
  "id": "hello",
  "name": "Hello",
  "version": "1.0.0",
  "publisher": "Your name",
  "homepage": "https://example.org/hello",
  "executable": {
    "win-x64": "plugin.cmd",
    "linux-x64": "plugin.sh"
  },
  "models": [
    {
      "id": "tiny",
      "title": "Tiny English model",
      "files": [
        {
          "file": "model.bin",
          "url": "https://example.org/hello/tiny/model.bin",
          "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
          "size": 77691904
        }
      ],
      "languages": ["en"],
      "setLanguage": false,
      "prompt": false,
      "licence": { "name": "MIT", "url": "https://example.org/hello/LICENSE" }
    }
  ]
}
```

`protocol`
:   `1`. Dictum refuses a plugin that needs another protocol, and says `it needs protocol 2; this Dictum speaks protocol 1`.

`id`
:   The plugin's id, which providers in `config.json` name it by: lowercase letters, digits and `-`, at most 40 characters, not starting with `-`. `whisper` is taken.

`name`, `version`
:   What Dictum shows when you add the plugin and in `dictum plugins list`. Neither may be empty.

`publisher`, `homepage`
:   Optional. `dictum plugins add` names the publisher, or says "an unknown publisher".

`executable`
:   The program to run on each system, by its runtime identifier: `win-x64` for Windows and `linux-x64` for Linux. The value is a file in the plugin's folder, or in a folder inside it, such as `bin/hello.exe`. It may be a script: a `.cmd` file on Windows, or a shell script with its execute bit set on Linux.

`models`
:   Optional: the models the plugin offers. Each has an `id` (lowercase letters, digits, `.`, `_` and `-`, at most 64 characters, starting with a letter or a digit), a `title`, the `languages` it knows, which must not be empty, and its `files`. Each file has a `file` name inside the model's folder (subfolders are fine, `..` is not), an `https` address in `url`, its `sha256` and its `size` in bytes. `setLanguage` and `prompt` say whether the model can be told the language and whether it uses a prompt: see [Languages and prompts](#language). Both are true unless you set them to false. `licence` is optional.

A provider that names a model by its id gets the folder `models/<plugin id>/<model id>` in Dictum's [downloads folder](../reference/files.md#downloads-folder). This version of Dictum does not download a plugin's models yet: put the files in that folder yourself, or give the provider the model's full path instead (see [Use it in a profile](#use)).

No text in `plugin.json` may contain control characters or invisible formatting characters. Dictum ignores fields it does not know, and accepts `//` comments and trailing commas. Write the field names as shown: they are case-sensitive.

`dictum plugins add` checks the file and lists every problem it finds, for example:

```text
error: plugin.json in C:\Users\you\plugins\hello is not valid: it has no program for win-x64
  plugin.json: it has no program for win-x64
```

## How Dictum starts it {#launch}

Dictum runs the program that `executable` names for your system, in the plugin's folder, with these options:

```text
<program> --port 0 [--model <path>] [--threads <n>]
```

`--port 0`
:   Listen on a free port that the system picks: bind port 0, then read which port you got.

`--model <path>`
:   The model to load, as a full path. For a model listed in the plugin's `plugin.json`, it is the folder `models/<plugin id>/<model id>` that holds the model's files, however many there are. When the provider names a full path instead, it is that file or folder. Without `--model`, the plugin only has to say who it is: see [What it must answer](#endpoints).

`--threads <n>`
:   How many threads to use, when the provider sets `threads`. Without it, the plugin decides.

Let the program accept options it does not know, as the example below does.

**The key.** Each start gets a new key: 64 lowercase hexadecimal characters, in the environment variable `DICTUM_PLUGIN_KEY`. Every request from Dictum carries it as `Authorization: Bearer <key>`. Answer any request without that header with 401. The key is in the environment rather than on the command line because other programs on the computer can read a process's command line.

**The ready line.** Once the program listens, it prints one line on stdout:

```json
{"dictumPlugin":1,"url":"http://127.0.0.1:50123"}
```

The address must be `http` on `127.0.0.1`, `::1` or `localhost`; Dictum refuses any other. Flush stdout after the line: a program whose output goes to a pipe often holds it back otherwise, as Python and C do.

Dictum then asks `GET /health` every 250 ms until it gets 200, and then `GET /dictum/v1/info`. From the start to that answer, the plugin has 60 seconds, model loading included. If it exits, does not get ready in time, announces another address, rejects the key or speaks another protocol, Dictum ends it and says why: see [When it does not work](#problems).

**Its output.** Every line on stdout except the ready line, and every line on stderr, goes to Dictum's log as `plugin <id>: <line>`. Dictum also keeps the last 20 lines, and shows the last of them when the plugin fails to start. A plugin that prints what it heard puts the dictated text in the log, so don't.

Dictum reads both streams as UTF-8, so write UTF-8. On Windows, Python before 3.15 writes to a pipe in the system's ANSI code page instead, unless `PYTHONUTF8=1` or `PYTHONIOENCODING=utf-8` is set, or the script calls `sys.stdout.reconfigure(encoding="utf-8")`. Python 3.15 and later write UTF-8 by default. The example below calls `reconfigure`, which does no harm in either.

**Its input.** The program's stdin is a pipe that only Dictum holds. Dictum writes nothing to it: it closes it when the plugin should exit. See [Exiting](#exit).

## What it must answer {#endpoints}

Every request carries `Authorization: Bearer <key>`. Answer with JSON.

`GET /health`
:   200 with `{"status":"ready"}` once the model is loaded. While it loads, 503 with `{"status":"loading"}`. Dictum asks again until it gets 200.

`GET /dictum/v1/info`
:   Who the plugin is and which model it runs:

    ```json
    {
      "protocol": 1,
      "name": "Hello",
      "version": "1.0.0",
      "model": { "id": "tiny", "languages": ["en"], "setLanguage": false, "prompt": false }
    }
    ```

    `protocol` must be 1. `model.languages`, `model.setLanguage` and `model.prompt` mean what they mean in `plugin.json`. Dictum shows the model's `id` in `dictum doctor` and keeps it in the history.

`POST /v1/audio/transcriptions`
:   One recording to turn into text, as `multipart/form-data`, in the shape of OpenAI's transcription API:

    - `file`: the recording, named `audio.wav`: WAV, 16-bit PCM, 16 kHz, mono.
    - `model`: the provider's `model`, as `config.json` writes it.
    - `response_format`: `json`.
    - `language` or `languages`: see [Languages and prompts](#language).
    - `prompt`: the profile's speech hint, with your words in it. It is sent only when there is one.

    Answer 200 with the text, and the language you heard if you know it:

    ```json
    {"text": "Hello from my plugin.", "language": "en"}
    ```

    Any other status is a failed dictation, and Dictum shows the status with the start of the body, as in `Plugin provider 'hello' answered 500 Internal Server Error: …`. A recording may take 60 seconds, and twice its length on top.

**Started without `--model`,** as `dictum plugins add` starts it, the plugin needs to answer only `/health`, with 200, and `/dictum/v1/info`, with `"model": null`.

**Ignore what you do not know.** Protocol 1 requires a plugin to ignore form fields and JSON fields it does not know, so that a later protocol can add some. Dictum ignores the fields it does not know in your answers.

## Languages and prompts {#language}

The profile's language decides which field the form carries:

| The profile's language | The form |
| --- | --- |
| One language, such as `uk` | `language=uk` |
| A list, such as `uk, en` | `languages=uk,en`: comma-separated, without spaces, the main language first |
| `auto` | neither |

What the plugin does with them depends on its engine:

- An engine that can choose from a list picks the language from `languages`, and prefers the first one when it is unsure.
- An engine that can be told one language, but cannot choose from a list, detects the language itself when it gets `languages`.
- An engine that cannot be told the language ignores both fields. Say so with `"setLanguage": false`, in `plugin.json` and in `/dictum/v1/info`.
- An engine without prompts ignores `prompt`. Say so with `"prompt": false`.

Answer the language you heard in `language` when you know it. When you leave it out, Dictum records the profile's language if the profile has only one.

## Exiting {#exit}

The plugin must exit when its stdin closes. Read stdin in a thread of its own, and exit as soon as it ends.

When Dictum no longer needs the plugin, because the daemon stops, saved settings no longer use it, or a command ends, Dictum:

1. closes the plugin's stdin;
2. waits 5 seconds for the plugin to exit;
3. if it has not, kills it with every process it started, and logs `Plugin hello (pid 4242) did not exit within 5 s of its input closing; ending it`.

If Dictum itself is killed, the pipe closes with it, so a plugin that exits on the end of stdin never outlives Dictum.

## A minimal plugin in Python {#example}

This plugin answers every recording with "hello from python". It needs Python 3.8 or later, and nothing outside its standard library. Make a folder, such as `hello`, with four files in it.

`plugin.py`, the plugin:

```python
import argparse
import hmac
import json
import os
import re
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# Dictum reads what a plugin prints as UTF-8. Before 3.15, Python on Windows writes
# to a pipe in the ANSI code page unless told otherwise, so switch to UTF-8 first.
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=0)
parser.add_argument("--model")
parser.add_argument("--threads", type=int)
args, _ = parser.parse_known_args()  # accept options this script does not know

KEY = os.environ.get("DICTUM_PLUGIN_KEY")
if not KEY:
    sys.exit("DICTUM_PLUGIN_KEY is not set: only Dictum starts this plugin.")


def model_info():
    if args.model is None:
        return None  # started by 'dictum plugins add', without a model
    return {
        "id": os.path.basename(args.model),
        "languages": ["en"],
        "setLanguage": False,
        "prompt": False,
    }


def read_form(handler):
    """The fields of a multipart/form-data request, by name, as bytes."""
    body = handler.rfile.read(int(handler.headers["Content-Length"]))
    boundary = handler.headers.get_param("boundary").encode()
    form = {}
    for part in body.split(b"--" + boundary)[1:-1]:
        head, _, value = part.partition(b"\r\n\r\n")
        name = re.search(rb'\bname="?([^";\r\n]+)', head).group(1).decode()
        form[name] = value[:-2]  # without the line break before the next boundary
    return form


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if not self.authorized():
            return
        if self.path == "/health":
            self.answer(200, {"status": "ready"})
        elif self.path == "/dictum/v1/info":
            self.answer(200, {"protocol": 1, "name": "Hello", "version": "1.0.0", "model": model_info()})
        else:
            self.answer(404, {"error": {"message": "not found"}})

    def do_POST(self):
        if not self.authorized():
            return
        if self.path != "/v1/audio/transcriptions":
            self.answer(404, {"error": {"message": "not found"}})
            return
        form = read_form(self)
        audio = form["file"]  # a WAV file: 16-bit PCM, 16 kHz, mono
        language = form.get("language", b"").decode() or None
        # A real plugin turns the audio into text here, in that language if it is set.
        self.answer(200, {"text": "hello from python"})

    def authorized(self):
        given = self.headers.get("Authorization", "").encode()
        if hmac.compare_digest(given, f"Bearer {KEY}".encode()):
            return True
        self.answer(401, {"error": {"message": "wrong key"}})
        return False

    def answer(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, format, *args):
        pass  # Dictum logs every line on stderr; one per request is too many


def exit_when_stdin_closes():
    sys.stdin.buffer.read()  # returns once Dictum closes the pipe, or dies
    os._exit(0)


server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
port = server.server_address[1]
print(json.dumps({"dictumPlugin": 1, "url": f"http://127.0.0.1:{port}"}), flush=True)
threading.Thread(target=exit_when_stdin_closes, daemon=True).start()
server.serve_forever()
```

What protocol 1 asks for, and where the script does it:

- **UTF-8 output:** `sys.stdout.reconfigure(encoding="utf-8")`, before anything is printed.
- **The key:** `authorized()` compares the header with `DICTUM_PLUGIN_KEY`, and answers 401 when it does not match.
- **The ready line:** `print(…, flush=True)`, once the server listens on the port the system gave it.
- **Exiting:** `exit_when_stdin_closes()` reads stdin to its end in a thread of its own, then exits with code 0.
- **Without a model:** `model_info()` answers `null`.
- **Unknown options and fields:** `parse_known_args()` skips options it does not know, and `read_form()` keeps every field while the handler reads only the ones it needs.

`log_message()` keeps the standard handler from writing a line to stderr for every request, which would put a line in Dictum's log for every health check. To make it a real plugin, load the model given in `--model` before the server starts, and replace the canned text with your engine's.

`plugin.cmd`, which starts it on Windows:

```bat
@python "%~dp0plugin.py" %*
```

`plugin.sh`, which starts it on Linux:

```sh
#!/bin/sh
exec python3 "$(dirname "$0")/plugin.py" "$@"
```

Make it executable with `chmod +x plugin.sh`. Both launchers run the Python that `PATH` finds where Dictum runs. If yours is elsewhere, or in a virtual environment, write its full path in place of `python` or `python3`.

`plugin.json`:

```json
{
  "protocol": 1,
  "id": "hello",
  "name": "Hello",
  "version": "1.0.0",
  "publisher": "Your name",
  "executable": {
    "win-x64": "plugin.cmd",
    "linux-x64": "plugin.sh"
  }
}
```

## Add it to Dictum {#add}

Give `dictum plugins add` the plugin's folder:

```sh
dictum plugins add ~/plugins/hello
```

On Windows, run `dictum` by its full path: see [The command line](../getting-started.md#command-line).

Dictum reads `plugin.json`, shows who the plugin says it is, and asks:

```text
Hello 1.0.0 by Your name from /home/you/plugins/hello
This plugin is a program that runs on your computer. Dictum did not check it. Add it only if you trust where it came from.
Add it? [y/N]:
```

If you answer `y`, Dictum runs the plugin once, without a model, to check that it starts and says who it is, and stops it again. Then it records the SHA-256 of the program, adds the folder to `plugins` in `config.json`, and says:

```text
Added Hello 1.0.0 (protocol 1).
see: https://docs.sigil.build/dictum/guides/write-a-plugin/#use
```

`--yes` adds the plugin without asking, for a script. If the plugin does not start, Dictum adds nothing: it shows the plugin's last lines, if it printed any, each after a `|`, and why it failed. See [When it does not work](#problems).

**After a change.** Before every start, Dictum compares the program's SHA-256 with the one it recorded. Once the program changes, whether you edited it or put in a new version, Dictum does not start it until you confirm it again: run `dictum plugins add` with the same folder. Dictum says `hello is already added; confirming it again.` and asks again. The next dictation starts the confirmed program, with no restart.

Dictum records the program that `executable` names, and only that file. With a launcher script, as in the example, that is `plugin.cmd` or `plugin.sh`, not `plugin.py`, so a change to `plugin.py` is not noticed. The record is kept in `plugin-trust.json` in Dictum's [data folder](../reference/files.md#data-folder), for this computer only: a `config.json` copied from another computer names plugins that are not confirmed here yet.

`dictum plugins list` shows each plugin you added, with its folder and its state: `confirmed`, `changed since you added it`, `not confirmed on this computer`, `the folder is missing`, or `plugin.json is not valid: …` followed by the problems. `dictum plugins remove hello` forgets the plugin and leaves its folder alone. It refuses while a speech provider uses the plugin.

## Use it in a profile {#use}

Settings shows a plugin provider but cannot create one yet. Add it to `config.json`: run `dictum config edit`, and add a provider under `stt`:

```json
"stt": {
  "hello": {
    "type": "plugin",
    "plugin": "hello",
    "model": "/home/you/models/tiny"
  }
}
```

`type`
:   `plugin`.

`plugin`
:   The plugin's id, from its `plugin.json`.

`model`
:   The model the plugin loads: the full path of a file or a folder, or the `id` of a model in the plugin's `plugin.json`. For an id, Dictum passes the folder `models/<plugin id>/<model id>` in the downloads folder: `%LOCALAPPDATA%\Dictum\models\hello\tiny` on Windows and `~/.local/share/dictum/models/hello/tiny` on Linux, unless you chose another downloads folder. In JSON, a Windows path needs doubled backslashes: `"C:\\Users\\you\\models\\tiny"`. The example plugin loads nothing, so any folder that exists will do, such as its own.

`threads`
:   Optional: 1 or more, passed as `--threads`.

Then make a profile use the provider: choose **Plugin hello** as its **Speech provider** in **Settings → Profiles**, or write the provider's id as the profile's `stt`:

```json
"stt": "hello"
```

Check it from a terminal. `dictum stt test hello` starts the plugin, records five seconds from the microphone, and prints what the plugin recognised; `--file memo.wav` sends a recording instead. `dictum doctor` shows the provider's row:

```text
  OK stt:hello              plugin Hello 1.0.0, model tiny, pid 4242
```

The [settings reference](../reference/settings.md#provider-plugin) describes the plugin's card in **Settings → Speech providers**.

## When it does not work {#problems}

`dictum doctor` and **Settings → System** say why a plugin does not start, with its last lines when it printed any. [A plugin does not start](../troubleshooting.md#plugin) lists each message with its fix, and how to read the plugin's lines in `dictum.log`.
