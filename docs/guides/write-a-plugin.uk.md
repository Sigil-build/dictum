# Створення плагіна

Плагін — це рушій розпізнавання мовлення, що працює окремою програмою поруч із Dictum. Dictum запускає його, надсилає йому кожен запис через HTTP на вашому комп'ютері й вставляє текст, яким той відповідає. Програму можна написати будь-якою мовою й запускати в ній будь-який рушій, аби вона дотримувалася протоколу 1, який описано на цій сторінці.

Ця сторінка — для тих, хто пише плагін. Якщо плагін вам дали, прочитайте [Додавання до Dictum](#add) і [Плагін у профілі](#use).

## Що таке плагін {#what}

Плагін — це тека, у якій лежать:

- `plugin.json`, де сказано, що це за плагін, яку програму запускати в кожній системі і з яких файлів вона складається;
- сама програма або скрипт, що її запускає, — для Windows, для Linux чи для обох.

Dictum запускає програму як дочірній процес, у теці плагіна й з вашими правами. Програма завантажує модель мовлення й відповідає на HTTP-запити від Dictum на адресі вашого комп'ютера, як-от `127.0.0.1`. Коду Dictum вона не використовує, тож писати її можна будь-якою мовою.

Коли вона працює:

- Демон запускає плагін, коли ним користується профіль: під час власного запуску й коли ви зберігаєте налаштування, яким плагін потрібен. Поки профіль ним користується, демон тримає плагін запущеним, із завантаженою моделлю. Якщо після збереження налаштувань плагін, тека, версія, модель і потоки лишилися ті самі, лишається й той самий процес.
- Команда `dictum`, якій потрібен плагін, як-от `dictum transcribe memo.wav --stt hello`, запускає власну копію й зупиняє її, коли завершується.
- Якщо плагін завершився, хоча його ніхто про це не просив, Dictum за секунду запускає його знову, до трьох разів за п'ять хвилин; запуск, що не вдався, теж серед них. Після цього плагін лишається зупиненим, доки ви не збережете налаштування, не виконаєте `dictum reload` або не перезапустите демон.

Whisper вбудовано в Dictum, це не окрема програма-плагін. Власні плагіни Dictum з'являться в одній із наступних версій. Ця сторінка — про плагіни, які ви пишете чи додаєте самі.

## plugin.json {#manifest}

`plugin.json` лежить у корені теки плагіна:

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
  "program": ["plugin.py"],
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
:   `1`. Плагін, якому потрібен інший протокол, Dictum відхиляє з повідомленням `it needs protocol 2; this Dictum speaks protocol 1`.

`id`
:   Ідентифікатор плагіна, за яким до нього звертаються розпізнавачі в `config.json`: малі латинські літери, цифри й `-`, щонайбільше 40 символів, і не `-` на початку. `whisper` зайнято.

`name`, `version`
:   Те, що Dictum показує, коли ви додаєте плагін, і в `dictum plugins list`. Жодне з них не може бути порожнім.

`publisher`, `homepage`
:   Необов'язкові. `dictum plugins add` називає видавця або пише `an unknown publisher`.

`executable`
:   Програма, яку треба запускати в кожній системі, за ідентифікатором середовища виконання (runtime identifier): `win-x64` для Windows і `linux-x64` для Linux. Значення — файл у теці плагіна або в теці всередині неї, як-от `bin/hello.exe`. Це може бути й скрипт: файл `.cmd` у Windows або скрипт оболонки з правом на виконання в Linux. У Windows запускачу `.cmd` чи `.bat` не можна передати шлях із деякими символами; які це символи, каже [приклад](#example).

`program`
:   Файли й теки, з яких складається програма, як шляхи всередині теки плагіна, як-от `["plugin.py", "lib"]`: скрипт, який запускає запускач, модулі, які він імпортує, бібліотеки, які завантажує. Перед кожним запуском Dictum перевіряє їх разом із `plugin.json` і програмою, яку називає `executable`: див. [Додавання до Dictum](#add). Тека охоплює всі файли в ній, крім тек `__pycache__`, які Python пише сам. Теку, що є посиланням, Dictum не відкриває; файл, що є посиланням, рахується як файл, на який він указує. Кожен запис — шлях усередині теки плагіна, можна й у підтеці, без частин `..`, `.` чи порожніх і не повний шлях, як-от `C:\hello\plugin.py`. Кожен має бути в теці, інакше Dictum пише `program lists 'lib', which is not in the plugin's folder`. Не вписуйте теку, у яку плагін пише сам, як-от для журналів: кожен запис туди рахувався б зміною. Поле необов'язкове, але без нього Dictum перевіряє лише `plugin.json` і цю програму, і `dictum plugins add` про це каже.

`models`
:   Необов'язково: моделі, які пропонує плагін. Кожна має `id` (малі латинські літери, цифри, `.`, `_` і `-`, щонайбільше 64 символи, на початку літера чи цифра), назву `title`, непорожній список мов `languages`, які вона знає, і свої файли `files`. Кожен файл має назву `file` всередині теки моделі (підтеки можна, `..` — ні), адресу `https` в `url`, свій `sha256` і розмір `size` у байтах. `setLanguage` і `prompt` кажуть, чи можна моделі задати мову і чи користується вона підказкою: див. [Мови й підказки](#language). Типово обидва дорівнюють true. `licence` необов'язкова.

Розпізнавач, що називає модель за її ідентифікатором, отримує теку `models/<plugin id>/<model id>` у [теці завантажень](../reference/files.md#downloads-folder) Dictum. Ця версія Dictum ще не завантажує моделей плагінів: покладіть файли в цю теку самі або вкажіть розпізнавачу повний шлях до моделі (див. [Плагін у профілі](#use)).

Жоден текст у цих полях не може містити керівних символів чи невидимих символів форматування, як-от пробілу нульової ширини чи символу зміни напрямку тексту. На поля, яких Dictum не знає, він не зважає, а коментарі `//` і коми в кінці списків приймає. Пишіть назви полів так, як показано: великі й малі літери в них розрізняються.

`dictum plugins add` перевіряє файл і перелічує всі знайдені проблеми, наприклад:

```text
error: plugin.json in C:\Users\you\plugins\hello is not valid: it has no program for win-x64
  plugin.json: it has no program for win-x64
```

## Як Dictum його запускає {#launch}

Dictum запускає програму, яку `executable` називає для вашої системи, у теці плагіна, з такими параметрами:

```text
<program> --port 0 [--model <path>] [--threads <n>]
```

`--port 0`
:   Слухати вільний порт, який вибере система: прив'язатися до порту 0, а тоді дізнатися, який порт дістався.

`--model <path>`
:   Повний шлях до моделі, яку треба завантажити. Для моделі з `plugin.json` плагіна це тека `models/<plugin id>/<model id>`, де лежать файли моделі, хоч скільки їх. Якщо розпізнавач натомість називає повний шлях, це той файл чи тека. Без `--model` плагін має лише сказати, хто він: див. [На що він має відповідати](#endpoints).

`--threads <n>`
:   Скільки потоків використовувати, якщо розпізнавач задає `threads`. Без цього параметра вирішує плагін.

Нехай програма приймає й параметри, яких не знає, як це робить приклад нижче.

**Ключ.** Кожен запуск отримує новий ключ: 64 шістнадцяткові символи малими літерами у змінній середовища `DICTUM_PLUGIN_KEY`. Кожен запит від Dictum несе його як `Authorization: Bearer <key>`. На запит без цього заголовка відповідайте 401. Ключ передається через середовище, а не в командному рядку, бо командний рядок процесу можуть прочитати інші програми на комп'ютері.

**Рядок готовності.** Щойно програма почала слухати, вона виводить у stdout один рядок:

```json
{"dictumPlugin":1,"url":"http://127.0.0.1:50123"}
```

Адреса має бути `http` на `127.0.0.1`, `::1` або `localhost`; будь-яку іншу Dictum відхиляє. Після цього рядка скиньте буфер stdout (flush): інакше програма, чий вивід іде в канал, часто його притримує — так роблять Python і C.

Потім Dictum кожні 250 мс питає `GET /health`, доки не отримає 200, а тоді — `GET /dictum/v1/info`. Від запуску до цієї відповіді плагін має 60 секунд разом із завантаженням моделі. Якщо він завершиться, не встигне підготуватися, назве іншу адресу, відхилить ключ чи говоритиме іншим протоколом, Dictum зупинить його й скаже чому: див. [Коли щось не працює](#problems).

**Вивід.** Кожен рядок stdout, крім рядка готовності, і кожен рядок stderr потрапляє в журнал Dictum як `plugin <id>: <line>`. Останні 20 рядків Dictum ще й зберігає і показує останні з них, коли плагін не запускається. Плагін, що виводить почуте, кладе надиктований текст у журнал, тож не робіть так.

Dictum читає обидва потоки як UTF-8, тож пишіть у UTF-8. Python до версії 3.15 у Windows натомість пише в канал у кодовій сторінці ANSI системи, якщо не задано `PYTHONUTF8=1` чи `PYTHONIOENCODING=utf-8` або скрипт не викликає `sys.stdout.reconfigure(encoding="utf-8")`. Python 3.15 і новіші типово пишуть у UTF-8. Приклад нижче викликає `reconfigure`, і в жодній із версій це не шкодить.

**Ввід.** stdin програми — канал, який тримає лише Dictum. Dictum нічого в нього не пише, а закриває його, коли плагіну час завершитися. Див. [Завершення](#exit).

## На що він має відповідати {#endpoints}

Кожен запит несе `Authorization: Bearer <key>`. Відповідайте у форматі JSON.

`GET /health`
:   200 з `{"status":"ready"}`, щойно модель завантажено. Поки вона завантажується — 503 з `{"status":"loading"}`. Dictum питає знову, доки не отримає 200.

`GET /dictum/v1/info`
:   Хто такий плагін і яку модель він запускає:

    ```json
    {
      "protocol": 1,
      "name": "Hello",
      "version": "1.0.0",
      "model": { "id": "tiny", "languages": ["en"], "setLanguage": false, "prompt": false }
    }
    ```

    `protocol` має бути 1. `model.languages`, `model.setLanguage` і `model.prompt` означають те саме, що й у `plugin.json`. Ідентифікатор моделі `id` Dictum показує в `dictum doctor` і зберігає в історії.

`POST /v1/audio/transcriptions`
:   Один запис, який треба перетворити на текст, як `multipart/form-data`, у формі API розпізнавання OpenAI:

    - `file`: запис під назвою `audio.wav`: WAV, 16-бітний PCM, 16 кГц, моно.
    - `model`: `model` розпізнавача так, як його записано в `config.json`.
    - `response_format`: `json`.
    - `language` або `languages`: див. [Мови й підказки](#language).
    - `prompt`: підказка профілю для розпізнавання, до якої входять ваші слова. Надсилається, лише коли вона є.

    Відповідайте 200 з текстом і мовою, яку почули, якщо ви її знаєте:

    ```json
    {"text": "Hello from my plugin.", "language": "en"}
    ```

    Будь-який інший код означає невдале диктування, і Dictum показує код разом із початком тіла відповіді, як-от `Plugin provider 'hello' answered 500 Internal Server Error: …`. На один запис плагін має 60 секунд і ще дві його тривалості.

**Запущений без `--model`,** як його запускає `dictum plugins add`, плагін має відповідати лише на `/health` (кодом 200) і на `/dictum/v1/info` (з `"model": null`).

**Не зважайте на те, чого не знаєте.** Протокол 1 вимагає, щоб плагін не зважав на поля форми й поля JSON, яких не знає: так наступний протокол зможе їх додати. На поля ваших відповідей, яких Dictum не знає, він теж не зважає.

## Мови й підказки {#language}

Мова профілю визначає, яке поле несе форма:

| Мова профілю | Форма |
| --- | --- |
| Одна мова, як-от `uk` | `language=uk` |
| Список, як-от `uk, en` | `languages=uk,en`: через кому, без пробілів, основна мова перша |
| `auto` | жодного з них |

Що плагін із ними робить, залежить від його рушія:

- Рушій, який уміє вибирати зі списку, бере мову з `languages`, а коли не певен — першу.
- Рушій, якому можна задати одну мову, але який не вміє вибирати зі списку, отримавши `languages`, визначає мову сам.
- Рушій, якому мову задати не можна, не зважає на обидва поля. Позначте це значенням `"setLanguage": false` у `plugin.json` і в `/dictum/v1/info`.
- Рушій без підказок не зважає на `prompt`. Позначте це значенням `"prompt": false`.

Мову, яку ви почули, повертайте в `language`, коли знаєте її. Якщо ви її не повернули, Dictum записує мову профілю, коли в профілі вона одна.

## Завершення {#exit}

Плагін має завершитися, коли закривається його stdin. Читайте stdin в окремому потоці й завершуйтеся, щойно він скінчиться. Запускайте цей потік першим, до завантаження моделі: якщо Dictum завершиться, поки велика модель ще вантажиться, плагін тоді завершиться одразу, а не після завантаження.

Коли плагін більше не потрібен — демон зупиняється, збережені налаштування ним уже не користуються або команда завершилася, — Dictum:

1. закриває stdin плагіна;
2. чекає 5 секунд, поки плагін завершиться;
3. якщо той не завершився, примусово завершує його разом з усіма процесами, які він запустив, і пише в журнал `Plugin hello (pid 4242) did not exit within 5 s of its input closing; ending it`.

Якщо примусово завершили сам Dictum, канал закривається разом із ним, тож плагін, що завершується з кінцем stdin, ніколи не переживе Dictum.

## Найпростіший плагін на Python {#example}

Цей плагін на кожен запис відповідає «hello from python». Йому потрібен Python 3.8 або новіший і нічого поза стандартною бібліотекою. Створіть теку, наприклад `hello`, і покладіть у неї чотири файли.

`plugin.py`, сам плагін:

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


def exit_when_stdin_closes():
    sys.stdin.buffer.read()  # returns once Dictum closes the pipe, or dies
    os._exit(0)


# First, before a model loads: a plugin that loads for a minute still exits at
# once when Dictum goes.
threading.Thread(target=exit_when_stdin_closes, daemon=True).start()


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


server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
port = server.server_address[1]
print(json.dumps({"dictumPlugin": 1, "url": f"http://127.0.0.1:{port}"}), flush=True)
server.serve_forever()
```

Що вимагає протокол 1 і де скрипт це робить:

- **Вивід у UTF-8:** `sys.stdout.reconfigure(encoding="utf-8")`, перш ніж щось виведено.
- **Ключ:** `authorized()` порівнює заголовок із `DICTUM_PLUGIN_KEY` і відповідає 401, якщо вони не збігаються.
- **Рядок готовності:** `print(…, flush=True)`, щойно сервер слухає порт, який дала система.
- **Завершення:** `exit_when_stdin_closes()` в окремому потоці, запущеному раніше за все інше, читає stdin до кінця, а тоді завершує програму з кодом 0.
- **Без моделі:** `model_info()` повертає `null`.
- **Невідомі параметри й поля:** `parse_known_args()` пропускає параметри, яких не знає, а `read_form()` зберігає всі поля, з яких обробник читає лише потрібні.

`log_message()` не дає стандартному обробнику писати в stderr рядок на кожен запит, бо тоді кожна перевірка `/health` додавала б рядок у журнал Dictum. Щоб зробити з цього справжній плагін, завантажте модель із `--model` після запуску потоку stdin і до запуску сервера й замініть заготовлений текст тим, що дасть ваш рушій.

`plugin.cmd`, що запускає його у Windows:

```bat
@python "%~dp0plugin.py" %*
```

cmd.exe виконує файл `.cmd` чи `.bat` і ще раз читає його командний рядок: `&` і `|` запускають ще одну команду, `%` розгортає змінну навіть у лапках, а `^ ! " < > ( )` змінюють те, що він виконує. Щоб шлях до моделі ніколи нічого не запустив, Dictum не запускає запускач `.cmd` чи `.bat` із параметром, у якому є `&`, `%`, `^`, `!`, `"`, `<`, `>`, `|`, `(` або `)`, як-от із текою моделі `C:\Users\you\R&D\tiny`. Натомість він пише `Plugin hello starts through plugin.cmd, which cannot be given …`. Не вживайте таких символів у шляху до моделі або запускайте плагін програмою, як-от `.exe`, а не скриптом.

`plugin.sh`, що запускає його в Linux:

```sh
#!/bin/sh
exec python3 "$(dirname "$0")/plugin.py" "$@"
```

Зробіть його виконуваним: `chmod +x plugin.sh`. Обидва запускачі беруть той Python, який знаходить `PATH` там, де працює Dictum. Якщо ваш Python в іншому місці чи у віртуальному середовищі, напишіть замість `python` або `python3` повний шлях до нього.

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
  },
  "program": ["plugin.py"]
}
```

## Додавання до Dictum {#add}

Передайте `dictum plugins add` теку плагіна:

```sh
dictum plugins add ~/plugins/hello
```

У Windows запускайте `dictum` за повним шляхом: див. [Командний рядок](../getting-started.md#command-line).

Dictum читає `plugin.json`, показує, ким називає себе плагін, і питає:

```text
Hello 1.0.0 by Your name from /home/you/plugins/hello
This plugin is a program that runs on your computer. Dictum did not check it. Add it only if you trust where it came from.
Add it? [y/N]:
```

Якщо в `plugin.json` немає `program` або цей список порожній, Dictum перед питанням про це каже: `plugin.json lists no program files, so Dictum checks only plugin.json and plugin.cmd for changes.`

Якщо відповісти `y`, Dictum один раз запускає плагін без моделі, щоб перевірити, що той запускається й каже, хто він, і знову його зупиняє. Потім записує SHA-256 файла `plugin.json`, програми й кожного файла, який перелічує `program`, додає теку до `plugins` у `config.json` і пише:

```text
Added Hello 1.0.0 (protocol 1).
see: https://docs.sigil.build/uk/dictum/guides/write-a-plugin/#use
```

`--yes` додає плагін без питання — для скриптів. Якщо плагін не запускається, Dictum нічого не додає: показує останні рядки плагіна, якщо той щось вивів, кожен після `|`, і причину. Див. [Коли щось не працює](#problems).

**Після змін.** Перед кожним запуском Dictum звіряє ці файли з записом. Щойно котрийсь із них змінився або в переліченій теці з'явився чи зник файл — ви відредагували плагін чи поставили нову версію, — Dictum не запускає плагін, доки ви не підтвердите його знову, і називає перший файл, що відрізняється: `Plugin hello changed since you added it (plugin.py).` Щоб підтвердити плагін, виконайте `dictum plugins add` з тією самою текою. Dictum напише `hello is already added; confirming it again.` і спитає знову. Якщо Dictum відмовився запускати змінену програму, наступне диктування запустить її, без перезапуску. Демон, у якому ще працює стара програма, так її й лишає: підтвердження нічого не змінює в `config.json`, а збереження налаштувань чи `dictum reload` лишають плагін, у якого тека, версія, модель і потоки ті самі. Щоб запустити нову програму, перезапустіть демон: вийдіть із Dictum через меню трею і запустіть його знову або виконайте `dictum shutdown`, а тоді `dictum daemon`.

Перелічіть у `program` кожен файл, який програма запускає чи завантажує: файла поза переліком Dictum не перевіряє. Запис зберігається в `plugin-trust.json` у [теці даних](../reference/files.md#data-folder) Dictum і стосується лише цього комп'ютера: `config.json`, скопійований з іншого комп'ютера, називає плагіни, яких тут ще не підтверджено.

`dictum plugins list` показує кожен доданий плагін, його теку й стан: `confirmed`, `changed since you added it`, `not confirmed on this computer`, `the folder is missing`, `plugin.json is not valid: …` із переліком проблем або `plugin.json cannot be read: …` із причиною. `dictum plugins remove hello` забуває плагін, а його теку лишає як є. Поки плагіном користується розпізнавач, ця команда відмовляється його забути.

## Плагін у профілі {#use}

Налаштування показують розпізнавача-плагіна, але створити його ще не вміють. Додайте його в `config.json`: виконайте `dictum config edit` і допишіть розпізнавача в `stt`:

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
:   Ідентифікатор плагіна з його `plugin.json`.

`model`
:   Модель, яку завантажує плагін: повний шлях до файла чи теки або `id` моделі з `plugin.json` плагіна. Для ідентифікатора Dictum передає теку `models/<plugin id>/<model id>` у теці завантажень: `%LOCALAPPDATA%\Dictum\models\hello\tiny` у Windows і `~/.local/share/dictum/models/hello/tiny` у Linux, якщо ви не вибрали іншу теку завантажень. У JSON зворотні скісні риски шляху Windows подвоюють: `"C:\\Users\\you\\models\\tiny"`. Плагін із прикладу нічого не завантажує, тож підійде будь-яка наявна тека, наприклад його власна.

`threads`
:   Необов'язково: 1 або більше, передається як `--threads`.

Потім скажіть профілю користуватися цим розпізнавачем: виберіть **Плагін hello** у полі **Розпізнавання мовлення** профілю в **Налаштування → Профілі** або запишіть ідентифікатор розпізнавача в `stt` профілю:

```json
"stt": "hello"
```

Перевірте з термінала. `dictum stt test hello` запускає плагін, записує п'ять секунд із мікрофона й виводить те, що розпізнав плагін; `--file memo.wav` надсилає натомість запис із файла. `dictum doctor` показує рядок розпізнавача:

```text
  OK stt:hello              plugin Hello 1.0.0, model tiny, pid 4242
```

Картку плагіна в **Налаштування → Розпізнавання мовлення** описано в [довіднику налаштувань](../reference/settings.md#provider-plugin).

## Коли щось не працює {#problems}

`dictum doctor` і **Налаштування → Система** кажуть, чому плагін не запускається, і показують його останні рядки, якщо він щось вивів. Кожне повідомлення з виправленням і те, як читати рядки плагіна в `dictum.log`, описано в розділі [Плагін не запускається](../troubleshooting.md#plugin).
