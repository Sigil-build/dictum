# Your words and replace rules

Whisper knows common words well, but it guesses at names, products and terms: "a valonia" for Avalonia, "nexus five" for Nexus5. Dictum gives you two tools for this:

- **Your words** come *before* recognition. Each profile has a list of words, and Dictum shows them to Whisper as a hint, so it leans towards your spelling.
- **Replace rules** come *after* recognition. They are a processor that finds text and replaces it, exactly as you say.

Start with your words. Add a replace rule when a word still comes out wrong the same way every time.

## Your words {#your-words}

### How your words work {#how-words-work}

Before each dictation, Dictum puts the profile's words into the *speech hint*, a short text Whisper reads before it listens. Whisper then prefers those spellings when the sound fits.

- The hint holds 400 characters, together with the profile's **Speech hint**. Words that do not fit are left out, from the end of the list. Put the most important words first.
- Words help only in the profile they belong to. Each profile has its own list.
- If the profile's language is **Auto**, a word list can pull language detection towards the list's own language. Set the profile's languages: see [Languages](languages.md).
- A remote speech server gets the same hint, as the request's `prompt`.
- The built-in LLM prompts do not use your words. A prompt of your own can, through `{{vocabulary}}`: see [LLM clean-up](llm-cleanup.md#prompt).

### In the setup wizard {#words-in-wizard}

Step 4 of the setup wizard is **Your words**.

1. Type your words in the box, one per line, or paste a list separated by commas: **One per line, or paste a comma-separated list**.
2. Click **Next**.

The words go to your first profile. When you run the wizard again, the box shows that profile's list, and the list becomes what the box holds when you finish.

### In Settings → Profiles {#words-in-profiles}

1. Open **Settings** → **Profiles** and select the profile.
2. Under **Your words**, type a word in **Add words — Enter or paste many** and press `Enter`. To add many at once, paste a list with one word per line, or separated by commas or semicolons.
3. Each word becomes a chip. The × on a chip removes it. **Words: 12** shows how many there are.

A word that is already in the list, in any letter case, is not added again.

**Speech hint**, under the words, is extra text Whisper reads before them, such as a sentence in the style you dictate. Most people leave it empty.

### From the command line {#vocab-command}

!!! note
    On Windows, the MSI does not put `dictum` on `PATH`. In PowerShell, run it by its full path, for example `& "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" vocab list`. In the AppImage, put `dictum` after the file name: `./dictum-<version>-linux-x64.AppImage dictum vocab list`.

`dictum vocab` edits the word list of the first profile, or of the profile you name with `--profile`. A running Dictum picks up each change at once.

```sh
dictum vocab add Avalonia Nexus5 "VS Code"
dictum vocab list
dictum vocab remove "VS Code"
dictum vocab clear
dictum vocab add --profile profile Kubernetes
```

- `add` puts words at the end of the list. Quote a phrase with spaces.
- `remove` takes words out, in any letter case, and warns about any that were not in the list.
- `clear` empties the list.
- `list` prints the list, one word per line.

Each change prints the profile and the count, such as `dictate: 3 word(s)`.

To add many words from a file, write one per line. Blank lines and lines that start with `#` are skipped:

```text
# Names
Avalonia
Nexus5
Kubernetes
```

```sh
dictum vocab import words.txt
```

`dictum vocab import -` reads the list from standard input.

When the hint runs out of room, `dictum vocab` warns you: `warning: the speech hint holds 400 characters, so these words are left out: …. Put the most important words first.`

## Replace rules {#replace-rules}

### Create a replace rules processor {#create-rules}

1. Open **Settings** → **Processors** and click **+ Replace rules**. The new processor is named `rules` (then `rules-2` and so on).
2. Fill in the table. Each row is one rule:

    | Column | What it does |
    | --- | --- |
    | **Find** | The text to find. |
    | **Regex** | **Find** is a .NET regular expression instead of plain text. |
    | **Replace** | The text to put in its place. Empty deletes what was found. |
    | **Match case** | For plain text: find only this exact letter case. Unticked, `avalonia` also finds `Avalonia`. |
    | **Whole word** | For plain text: match only whole words, so `main` leaves `mainframe` alone. |
    | **On** | Untick to keep the rule in the list without applying it. |

3. To add a rule, type in **Find** on the blank row at the end. The rules apply in order, from the top.
4. Make sure a profile runs the processor. A new processor joins every profile whose processing chain is empty. Otherwise the list marks it **In no profile**: pick a profile in **Add to profile…**, as [LLM clean-up](llm-cleanup.md#add-to-profile) shows. See also [Troubleshooting](../troubleshooting.md#processor-in-no-profile).

Some rules that fix what Whisper writes:

| Find | Regex | Replace | Match case | Whole word |
| --- | --- | --- | --- | --- |
| `a valonia` | | `Avalonia` | | yes |
| `nexus five` | | `Nexus5` | | yes |
| `(?i)\bvs code\b` | yes | `VS Code` | | |

A regular expression follows its own rules: **Match case** and **Whole word** do not apply to it. Start it with `(?i)` to ignore letter case, and use `\b` for word edges. `$1` in **Replace** puts back the first group in brackets. A regular expression that takes longer than 200 ms on one text is skipped.

### Import rules from a CSV file {#import-csv}

**Import from CSV…** adds rules from a file with the columns Find, Replace, and optionally Match case and Whole word:

```text
Find,Replace,Match case,Whole word
a valonia,Avalonia,,yes
nexus five,Nexus5,,yes
```

- A first row whose first cell is `Find` or `Знайти` is skipped, and so are blank lines.
- Cells are separated by commas or by semicolons, whichever the first line has more of. Excel writes semicolons on some systems.
- `true`, `yes`, `1`, `x`, `так` or `+` ticks a box.
- Imported rules are plain text, not regular expressions. Lines that cannot be read are named and skipped.

### Their order in the processing chain {#chain-order}

A profile's **Processing chain** runs its steps from top to bottom, and each step gets the text the step above returned. Where you put the rules matters when the profile also has an LLM prompt:

- **Rules before the LLM prompt**: the language model sees your corrected words and keeps them in context.
- **Rules after the LLM prompt**: your rules have the last word, whatever the model wrote.

To change the order, open **Settings** → **Profiles** and select the profile. Under **Processing chain**, each step shows its number, its name and its kind, such as `rules` with **Rules: 3** and `cleanup` with **LLM prompt**. Drag a step by its handle, or focus it and press `Alt+↑` or `Alt+↓`.

To check the result, run text through the profile's chain:

```sh
dictum process "i build the ui in a valonia" --profile dictate
```

`--pipeline rules` runs only the rules, and `--json` shows how long each step took.
