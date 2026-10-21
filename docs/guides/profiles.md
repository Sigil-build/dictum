# Profiles

A profile is a set of dictation choices with its own hotkey. Dictum starts with one profile, `dictate`, on `Ctrl+Alt+Space`. Add more when you dictate in different ways: Ukrainian with clean-up on one hotkey, English without clean-up on another, for example.

You edit profiles in **Settings** → **Profiles**. Click the Dictum icon in the tray, or choose **Settings** in its menu, to open Settings. The [Settings reference](../reference/settings.md#profiles) describes every field.

## What a profile holds {#what-a-profile-holds}

| Field | What it sets |
| --- | --- |
| **Name** | The name the Profiles list shows. |
| **Hotkey** | The keys that start this profile. A profile can have none; you then start it from the command line. |
| **Mode** | **Push to talk** or **Toggle**: see [Hold or toggle](#hold-or-toggle). |
| **Languages** | One language, several with a main one, or Auto: see [Languages](languages.md). |
| **Your words** | Names and terms Whisper should spell your way: see [Your words and replace rules](words-and-rules.md). |
| **Speech hint** | Extra text Whisper sees before your words. Most people leave it empty. |
| **Processing chain** | The processors that work on the text, in order: replace rules, an LLM prompt. Empty means the text is pasted as recognised. |
| **Output** | **Paste into the focused app**, **Type it, key by key**, or **Copy to clipboard only**. |
| **Paste shortcut** | **Automatic**, `Ctrl+V` or `Ctrl+Shift+V`. **Automatic** uses `Ctrl+Shift+V` in terminals Dictum recognises. |
| **Speech provider** | Which recogniser and model the profile uses, such as **Local Whisper · large-v3-turbo-q8_0**. |

Every profile also has an *id*, such as `dictate`. The tray menu and the command line name profiles by their id. Settings gives a profile you add the id `profile`, then `profile-2` and so on, and shows it greyed in the **Name** box while the name is empty. `dictum profiles list` prints every id.

The model belongs to the speech provider, so profiles that use the same provider use the same model. To give one profile another model, add a provider with **+ Local Whisper** on the **Speech providers** page, choose its model there, and pick that provider as the profile's **Speech provider**.

## Hold or toggle {#hold-or-toggle}

**Push to talk**
:   Dictum records while you hold the hotkey. Let go, and the text is pasted. This suits short phrases, and it is the default.

**Toggle**
:   Press the hotkey once to start, and press it again to stop and paste. You do not have to hold the keys, which suits long dictation.

One dictation runs at a time. While one profile records, the other profiles' hotkeys do nothing.

## Two profiles with different hotkeys {#two-profiles}

This example keeps `dictate` for Ukrainian and English on `Ctrl+Alt+Space`, and adds a profile for English only, on `Ctrl+Alt+E`, in Toggle mode, without clean-up.

1. Open **Settings** → **Profiles**.
2. Select **Dictate** in the list, then click **+ New profile**. The new profile copies the speech provider and the languages of the selected one. It has no hotkey, no words and an empty processing chain, and it is selected.
3. In **Name**, type `English`.
4. Click the **Hotkey** box ("Click, then press keys") and press `Ctrl+Alt+E`. While the box listens, Dictum pauses its hotkeys. `Esc` cancels, and `Backspace` removes the hotkey.
5. Under **Mode**, choose **Toggle**.
6. Under **Languages**, choose **One**, then **English** in the list.
7. Leave **Processing chain** as it is: "No processing: the text is used as recognised."
8. Wait a moment. Settings saves the profile half a second after your last change, and the running Dictum registers the new hotkey.
9. Try both. Put the cursor in a text editor:
    - hold `Ctrl+Alt+Space`, say a sentence in Ukrainian, and let go;
    - press `Ctrl+Alt+E`, say a sentence in English, and press `Ctrl+Alt+E` again.

If the hotkey does not take:

- A red line such as `Ctrl+Alt+E is already used by another profile` means another profile has the same keys. Choose other keys: nothing is saved while a field is red.
- "The daemon could not register this hotkey; another app may already use it." means another program holds the keys. Choose other keys, or see [Troubleshooting](../troubleshooting.md#hotkeys).
- On Wayland, the desktop asks you once to allow each new shortcut. Allow it.

To delete a profile, select it and click **Delete profile**. The last profile cannot be deleted.

## Switch profiles in the tray {#tray}

The tray menu lists your profiles under **Profile**, each by its id with its languages, such as `dictate (uk, en)`. A tick marks the selected profile. Click another one to select it.

The selected profile matters only where no profile is named:

- `dictum toggle` and `dictum start` without `--profile` start it. This is how a key you bind to `dictum toggle` in your desktop's settings picks its profile.
- The tray icon's tooltip shows its id and hotkey, such as `Dictum · dictate · Ctrl+Alt+Space`.

Each profile's own hotkey always starts that profile, whichever one is selected. The choice is not saved: when Dictum starts again, the first profile is selected.

## Profiles from the command line {#command-line}

!!! note
    On Windows, the MSI does not put `dictum` on `PATH`. In PowerShell, run it by its full path, for example `& "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" profiles list`. In the AppImage, put `dictum` after the file name: `./dictum-<version>-linux-x64.AppImage dictum profiles list`.

### List and inspect profiles {#list-and-show}

```sh
dictum profiles list
```

It prints one line per profile: id, hotkey, mode, speech provider, languages and processing chain.

```text
dictate              Ctrl+Alt+Space     PushToTalk  stt=local lang=uk, en pipeline=[cleanup]
profile              Ctrl+Alt+E         Toggle      stt=local lang=en pipeline=[]
```

```sh
dictum profiles show dictate
```

It prints one profile in full, with its speech provider and each processor:

```text
id:         dictate
name:       Dictate
hotkey:     Ctrl+Alt+Space (PushToTalk)
stt:        local  whisper-local  model=ggml-large-v3-turbo-q8_0.bin runtime=auto vad=true
language:   uk, en
sttPrompt:  -
vocabulary: Avalonia, Nexus5
pipeline:   cleanup
  cleanup          llm, http://localhost:8080/v1 model=local prompt=cleanup
output:     paste
```

### Change a hotkey {#set-hotkey}

```sh
dictum profiles set-hotkey profile "Ctrl+Alt+E"
```

It saves the hotkey and prints `profile: Ctrl+Alt+E`. A running Dictum picks it up at once. Write the keys joined by `+`: `Ctrl`, `Alt`, `Shift` and `Win` (also `Super` or `Meta`), then one key, such as `Space`, `E`, `F9` or `` ` ``. A combination another profile already uses is refused.

### Start a dictation with a given profile {#toggle-profile}

```sh
dictum toggle --profile profile
```

The first run starts recording with that profile, and the next one stops it and pastes the text. `dictum start --profile profile` only starts. `dictum stop` stops the running dictation and pastes, and `dictum cancel` stops it and throws the recording away; neither needs a profile.

These commands work with any profile, with or without a hotkey. On a desktop where Dictum cannot register hotkeys itself, such as GNOME before version 48, bind a key to `dictum toggle --profile <id>` in the desktop's keyboard settings.
