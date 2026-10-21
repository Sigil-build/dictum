# Languages

Whisper, the speech model Dictum uses, knows about 99 languages. Each [profile](profiles.md) has its own language setting, in one of three modes:

- **One language.** Whisper always writes in it. This is the most reliable choice if you dictate in one language.
- **Several languages.** For each dictation, Whisper picks one of them. The first one is the *main language*: it wins whenever Whisper is unsure.
- **Auto.** Whisper detects any language on its own. It is slower and less accurate for mixed speech.

This page shows where to set the languages, how the main language decides, and what changed in 0.4.

## Choose your languages in the setup wizard {#in-the-wizard}

The setup wizard opens on the first start. You can run it again later: **Settings** → **General** → **Setup wizard** → **Run setup again**.

1. Step 1, **Which languages do you dictate in?**, lists every language Whisper knows. On a first setup, your system's language is already ticked, and English too.
2. Tick each language you dictate in. Type in **Find a language…** to find one by its own name, its English name or its code, such as `uk`.
3. One of the ticked languages wears the **Main** badge. To make another one main, click **Make main** on its row.
4. If you only ever dictate in one language, untick the others.
5. To let Whisper detect any language instead, tick **Or let Whisper detect any language (slower, less accurate for mixed speech)**.
6. Click **Next**.

The wizard sets the languages of your first profile. Other profiles keep their own: change them in **Settings** → **Profiles**.

## Change the languages of a profile {#in-profiles}

1. Click the Dictum icon in the tray, or choose **Settings** in its menu.
2. Open **Profiles** and select the profile on the left.
3. Under **Languages**, pick the mode: **One**, **Several** or **Auto**.
   - **One**: choose the language from the list.
   - **Several**: each language is a chip. The first chip is marked **main**. Click **Make main** on another chip to put it first. Add a language with **Add a language**, and remove one with the × on its chip. The last chip cannot be removed.
   - **Auto**: "Whisper detects the language of each dictation."

There is no Save button. Settings saves a change half a second after you make it, and the running Dictum picks it up for the next dictation.

In `config.json`, the setting is the profile's `language`. With several languages, the main one comes first:

```json
"language": "uk, en"
```

## How the main language decides {#main-language}

With several languages, Whisper first listens to the recording and says which of *your* languages it hears, and how sure it is. Dictum then picks the language to write in:

1. If Whisper hears the main language, Dictum uses it.
2. If Whisper hears another language from your list and is at least 50% sure of it, Dictum uses that language.
3. In every other case, Dictum uses the main language.

For example, with `uk, en`:

- You dictate in Ukrainian: the text comes out in Ukrainian.
- You dictate a whole sentence in English: Whisper is usually sure of it, and the text comes out in English.
- Whisper hears English but is only 6% sure, which happens with Ukrainian speech: the text comes out in Ukrainian.

So make the main language the one you dictate in most.

Each choice is written to Dictum's log, `dictum.log` in the `logs` folder (`%LOCALAPPDATA%\Dictum\logs` on Windows, `~/.local/share/dictum/logs` on Linux). A line where the main language won looks like this:

```text
2026-10-21 10:15:02.481 +02:00 DEBUG Whisper 'local' picked 'uk' from uk, en (detected 'en', p=0.06; the first language has priority, and another one needs p >= 0.50) in 38 ms
```

## Closely related languages {#similar-languages}

Whisper often mixes up languages that sound alike: Ukrainian, Belarusian and Russian; Polish, Czech and Slovak; Spanish, Portuguese and Galician; Norwegian (both forms), Danish and Swedish; Croatian, Serbian and Bosnian; Indonesian and Malay; Hindi and Urdu.

If you put two of them in one profile, the wizard and the **Profiles** page warn you. The text may come out in the other alphabet or spelling. Keep the one you use most in this profile, and put the other in a profile of its own, with its own hotkey: [Profiles](profiles.md#two-profiles) shows how.

## Why words came out "translated" before 0.4 {#translated-before-0-4}

Before 0.4, Dictum took whichever of the profile's languages Whisper rated highest, however unsure Whisper was. Ukrainian speech often makes Whisper think of Russian first. With `uk, en` and no Russian in the list, English could end up on top with a probability as low as 6%. Whisper then wrote your Ukrainian speech in English: the text came out translated.

Since 0.4, the main language wins unless Whisper is at least 50% sure of another one, as described above. If your text still comes out in the wrong language, see [Troubleshooting](../troubleshooting.md#translated-words).

## A remote speech server gets only the main language {#remote-server}

A profile can use a remote server for recognition instead of Whisper on your computer: you add one with **+ Remote server** on the [**Speech providers**](../reference/settings.md#speech-recognition) page of Settings, and choose it as the profile's **Speech provider**.

An OpenAI-compatible server takes one language per request, not a list. So with several languages, Dictum sends only the main one, and the server writes everything in it. The **Profiles** page shows a warning under **Languages**, in the config validator's words, for example:

```text
'remote' takes a single language, so with a list the server gets the first language, 'uk'
```

With **Auto**, Dictum sends no language, and the server detects it.

If you dictate in two languages through a remote server, make two profiles, each with one language.

## Try it from the command line {#command-line}

`dictum transcribe` runs a WAV file through recognition, so you can compare settings on the same recording:

```sh
dictum transcribe memo.wav --lang "uk, en" --raw
dictum transcribe memo.wav --lang uk --raw
```

`--lang` takes one code, a list such as `"uk, en"`, or `auto`. `--raw` skips the profile's processors, so you see what Whisper wrote. `dictum profiles show dictate` prints a profile's `language:`.

!!! note
    On Windows, the MSI does not put `dictum` on `PATH`. In PowerShell, run it by its full path, for example `& "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" transcribe memo.wav --raw`. In the AppImage, put `dictum` after the file name: `./dictum-<version>-linux-x64.AppImage dictum transcribe memo.wav --raw`.
