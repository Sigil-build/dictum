# Getting started

This page takes you from the download to your first dictation. Most of the time goes into downloading the speech model.

## What you need {#requirements}

- **Windows 11 or Linux** on a 64-bit x86 computer.
- **A processor with AVX2:** Intel Haswell, AMD Zen or newer. Dictum does not start on an older one.
- **A microphone.**
- **Disk space for a speech model:** about 265 MB for the model the setup recommends for the processor, about 875 MB for the one it recommends for a graphics card.
- **A graphics card is optional.** Whisper uses one through Vulkan when its driver supports it, and the processor otherwise. The setup recommends the larger model for a graphics card, unless the card has less than 2 GB of memory.

On Linux you also need:

- **glibc 2.38 or newer for hotkeys on X11:** Ubuntu 24.04, Fedora 39, current Arch or newer. The X11 hotkeys also use the libraries libXtst, libXt, libXrandr and libxkbcommon, which a desktop install already has.
- **A clipboard tool to paste the text:** `wl-clipboard` on Wayland, `xclip` or `xsel` on X11.
- **On Wayland, `xdg-desktop-portal`** with your desktop's portal backend. KDE Plasma and GNOME install theirs. Dictum asks the portal for the hotkey and for permission to press Ctrl+V: see [Wayland and X11](#wayland-and-x11).
- **Optional:**
    - `xprop` on X11, so that Dictum recognises a terminal and pastes there with Ctrl+Shift+V;
    - GNOME Keyring or KWallet, to keep API keys out of the config file;
    - `ydotool` or `wtype`, to press Ctrl+V on Wayland when the desktop has no RemoteDesktop portal.

Install the clipboard tools with your package manager:

```sh
# Debian, Ubuntu
sudo apt install wl-clipboard xclip
# Fedora
sudo dnf install wl-clipboard xclip
# Arch, CachyOS
sudo pacman -S wl-clipboard xclip
```

## Download {#download}

Download Dictum from the [releases page](https://github.com/Sigil-build/dictum/releases/latest). Each release has these files, where `<version>` is the release's version, such as `0.4.0`:

| File | What it is |
| --- | --- |
| `dictum-<version>-win-x64.msi` | Windows 11: the installer. Take this one unless you want no installer. |
| `dictum-<version>-win-x64.zip` | Windows 11: the same programs, without an installer. |
| `dictum-<version>-linux-x64.AppImage` | Linux: one file that runs as it is. |
| `dictum-<version>-linux-x64.tar.gz` | Linux: installs for your user with `install.sh`. The Arch package is built from it too. |
| `PKGBUILD` | Arch Linux and CachyOS: builds a pacman package from the tarball. |
| `SHA256SUMS` | The SHA-256 sums of the four `dictum-<version>` packages. |

## Check the download {#check-download}

Before you install, you can check the file in two ways.

**Its SHA-256 sum.** In the folder you downloaded to, compare the file with its line in `SHA256SUMS`. On Linux, this prints `OK` after each file it finds:

```sh
sha256sum --check --ignore-missing SHA256SUMS
```

On Windows, this prints `True` when the MSI matches:

```powershell
(Get-FileHash .\dictum-0.4.0-win-x64.msi).Hash -eq ((Select-String 'win-x64.msi' .\SHA256SUMS).Line -split ' ')[0]
```

**Its VirusTotal report.** The notes of each release on the [releases page](https://github.com/Sigil-build/dictum/releases) have a VirusTotal section with a link for each package. [VirusTotal](https://www.virustotal.com/) scans a file with dozens of antivirus engines, and the report shows what each of them found. Dictum is not signed yet and is little known, so one or two engines may flag it as suspicious while the rest find nothing.

## Install {#install}

The commands on this page use version 0.4.0. Put in the version you downloaded.

=== "Windows 11"

    **The installer**

    1. Open `dictum-<version>-win-x64.msi`. It asks no questions.
    2. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**. See [If Windows blocks Dictum](#windows-blocks).
    3. The installer needs no administrator rights. It installs Dictum for your user only, into `%LOCALAPPDATA%\Programs\Dictum`, and adds Dictum to the Start menu.
    4. When it is done, Dictum starts and opens its setup. Go on with [the first run](#first-run).

    **The zip, without an installer**

    1. Extract `dictum-<version>-win-x64.zip`. It holds one folder, `dictum-<version>-win-x64`.
    2. Move that folder to a place that stays put and give it a name without the version, for example `C:\Tools\Dictum`. Not your Downloads folder: start at login remembers where the programs are, and an update goes into the same folder.
    3. Run `dictum-gui.exe` from that folder. Dictum opens its setup.

=== "Linux"

    Pick one of the three.

    **AppImage:** one file, and nothing is installed.

    1. Move the file to a place that stays put, give it a name without the version, and make it executable:

        ```sh
        mkdir -p ~/Applications
        mv ~/Downloads/dictum-0.4.0-linux-x64.AppImage ~/Applications/Dictum.AppImage
        chmod +x ~/Applications/Dictum.AppImage
        ```

    2. Run it. Dictum opens its setup.

        ```sh
        ~/Applications/Dictum.AppImage
        ```

    Start at login remembers this path, and an update replaces the file under the same name. The command line is inside the AppImage too: put `dictum` after the file name, as in `~/Applications/Dictum.AppImage dictum status`.

    **Tarball, for your user:** installs into `~/.local/lib/dictum`.

    1. Unpack the tarball and run its `install.sh`:

        ```sh
        cd ~/Downloads
        tar -xzf dictum-0.4.0-linux-x64.tar.gz
        ./dictum-0.4.0-linux-x64/install.sh
        ```

    2. `install.sh` copies Dictum to `~/.local/lib/dictum`, links `dictum` and `dictum-gui` into `~/.local/bin` and adds Dictum to the application menu. It needs no root. Afterwards you can delete the tarball and the unpacked folder.
    3. Start Dictum from the application menu. It opens its setup.

    **Arch Linux, CachyOS:** a pacman package, installed into `/opt/dictum`.

    1. Put `PKGBUILD` and `dictum-<version>-linux-x64.tar.gz` from the same release in one folder.
    2. In that folder, run:

        ```sh
        makepkg -si
        ```

        `makepkg` checks the tarball against the sum in `PKGBUILD`, builds the package `dictum-bin` and installs it with pacman, which asks for your password.

    3. Start Dictum from the application menu. It opens its setup.

## If Windows blocks Dictum {#windows-blocks}

Dictum's installer and programs are not signed yet, so Windows warns about them:

- **SmartScreen** says "Windows protected your PC" the first time you open the MSI or `dictum-gui.exe`. Click **More info**, then **Run anyway**.
- **Smart App Control** blocks apps that are not signed and has no exception for a single app. While it is on, Dictum cannot be installed or run. Windows Security → App & browser control → Smart App Control settings shows whether it is on.

[Troubleshooting](troubleshooting.md#smartscreen) has more.

## First run: the setup wizard {#first-run}

The first time Dictum starts, it opens **Set up Dictum**, a wizard of eight steps: **Languages**, **Speech model**, **Text clean-up**, **Your words**, **Start at login**, **Downloads**, **Downloading** and **Ready**. You pick the languages you dictate in, and Dictum recommends a speech model for your graphics card or processor. Clean-up with a language model and your own words are optional. Nothing is downloaded until you confirm the list of files, and each file is checked against its SHA-256 sum. You can close the window while the files download: the download goes on, and continues after a restart. The [setup wizard reference](reference/setup-wizard.md) explains every step.

If you close the wizard before the end, it keeps your answers: choose **Settings** in the tray menu to open it again at the same step. Closed while its files download, it comes back by itself when they are in.

Dictum then lives in the tray, by the clock. Windows hides new tray icons behind the ^ arrow, and the **Ready** step shows how to keep Dictum's icon in view, so that you can see when it records. On GNOME, the icon needs the "AppIndicator and KStatusNotifierItem Support" extension.

## Your first dictation {#first-dictation}

1. Click where the text should go: a text editor, a browser's address bar, a chat.
2. Hold `Ctrl+Alt+Space`. Dictum plays a short tick, and a small overlay at the bottom of the screen shows that it is listening.
3. Speak.
4. Release the keys. A moment later, the text appears where the cursor is.

![The overlay while Dictum listens](images/overlay-listening.png#gh-light-mode-only)
![The overlay while Dictum listens](images/overlay-listening-dark.png#gh-dark-mode-only)

The **Ready** step of the wizard has a box, **Try dictation**, to try this before you close it. The first dictation after Dictum starts waits until the speech model is in memory.

If no text appears, run `dictum doctor` (on Windows by its full path, see [The command line](#command-line)) and see [Troubleshooting](troubleshooting.md#doctor). Dictum cannot paste into an app that runs as administrator: see [Troubleshooting](troubleshooting.md#administrator).

## Wayland and X11 {#wayland-and-x11}

On Linux, how Dictum gets the hotkey and pastes depends on your session. To see which one you are in, run:

```sh
echo $XDG_SESSION_TYPE
```

- **X11:** the hotkeys and pasting work without any setup.
- **Wayland:** an app may not watch the keyboard or press keys in other apps by itself, so Dictum asks the desktop portal, `xdg-desktop-portal`:
    - for the hotkey, through the GlobalShortcuts portal, which KDE Plasma 6, GNOME 48 and newer, and Hyprland have. The desktop asks you once to allow the shortcut.
    - for pressing Ctrl+V, through the RemoteDesktop portal. The desktop asks you once whether Dictum may control the keyboard, and Dictum remembers the answer. Without that portal, Dictum uses `ydotool` when it is installed. On Sway and Hyprland the order is different: `wtype` first, then `ydotool`, then the portal.
- **No GlobalShortcuts portal**, as on GNOME before 48: bind a key in your desktop's keyboard settings to the command `dictum toggle`. Press it once to start and once more to stop and paste. In Sway, you can hold the key to talk:

    ```text
    bindsym --no-repeat Ctrl+Alt+space exec dictum start
    bindsym --release Ctrl+Alt+space exec dictum stop
    ```

- **Terminals on Wayland:** Dictum cannot see which app has the focus, so it pastes with Ctrl+V everywhere. A terminal wants Ctrl+Shift+V: set **Paste shortcut** in a profile of its own, in Settings → **Profiles** (see [Settings](reference/settings.md#profiles)).

`dictum doctor` lists what works, and what to install or allow when something does not.

## Set up without the desktop app {#terminal-setup}

If you prefer the terminal, or your desktop has no tray, set Dictum up with `dictum init` instead of the wizard:

```sh
dictum init
```

It asks the same questions one after another: your languages, the speech model, clean-up with a language model, your words, and whether to start Dictum at login. Then it lists the files to download, and downloads them after you agree.

Then run Dictum without the desktop app, in the foreground. `Ctrl+C` stops it.

```sh
dictum daemon
```

`dictum daemon` needs a desktop session for the hotkeys and pasting: Windows, or Wayland or X11 on Linux. For a script, `dictum init --yes` takes the options you give, such as `--language "uk, en"`, and the recommendations for the rest; `dictum init --help` lists them.

## The command line {#command-line}

On Linux, the tarball and the Arch package put `dictum` on your PATH. If `~/.local/bin` is not on it, `install.sh` says so. With the AppImage, put `dictum` after the file name: `~/Applications/Dictum.AppImage dictum doctor`.

On Windows, `dictum.exe` sits next to `dictum-gui.exe` and is not on PATH. After the MSI, run it by its full path in PowerShell:

```powershell
& "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" doctor
```

After the zip, use your folder instead, for example `& "C:\Tools\Dictum\dictum.exe" doctor`.

To type plain `dictum`, add the folder to your Path:

1. Open Start, type "environment" and choose **Edit environment variables for your account**.
2. Under **User variables**, select **Path** and click **Edit**.
3. Click **New**, enter `%LOCALAPPDATA%\Programs\Dictum` (or your zip folder), and click **OK** in both windows.
4. Open a new PowerShell window and check:

    ```powershell
    if (Get-Command dictum -ErrorAction SilentlyContinue) { 'dictum is on PATH' } else { 'dictum is not on PATH' }
    ```

## Start at login {#start-at-login}

The wizard's **Start at login** step turns it on. Later, use **Start Dictum at login** in Settings → **General**. Dictum then starts its tray app when you log in, and the tray app starts the daemon. Without the desktop app, `dictum daemon --install-autostart` starts the daemon alone at login.

With the zip or the AppImage, the login item remembers where Dictum is. If you move it, turn start at login off and on again. [Start at login](guides/start-at-login.md) has the details.

## Next steps {#next}

- [Languages](guides/languages.md): dictate in more than one language.
- [Profiles](guides/profiles.md): more than one setup, each with its own hotkey.
- [LLM clean-up](guides/llm-cleanup.md): let a language model fix punctuation and typos.
- [Your words and replace rules](guides/words-and-rules.md): names and terms that Whisper gets wrong.
- [Update and remove](guides/update-and-remove.md) and [where Dictum keeps its files](reference/files.md).
- [Troubleshooting](troubleshooting.md), when something does not work.
