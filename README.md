# Dictum

Voice to text for Windows and Linux: hold a hotkey, speak, and the text lands in the app you are typing in. Speech recognition runs on your machine with Whisper (on the GPU through Vulkan when it can); nothing is sent anywhere unless you add a cloud provider yourself.

Dictum has two programs, installed together:

- `dictum-gui`: the tray app with the setup window, the settings and the on-screen overlay.
- `dictum`: the command line and the background daemon that listens for the hotkey.

## Install

### Windows 11

1. Download `dictum-<version>-win-x64.msi` and open it. It installs for your user only, into `%LOCALAPPDATA%\Programs\Dictum`, without asking for admin rights, and starts Dictum when it is done.
2. The installer is not signed yet:
   - **SmartScreen** says "Windows protected your PC". Click "More info", then "Run anyway".
   - **Smart App Control**, when it is on (Windows Security → App & browser control), blocks unsigned apps outright. Dictum cannot be installed while it is on.

Prefer no installer? Unzip `dictum-<version>-win-x64.zip` into a folder that stays put (for example `C:\Tools\Dictum`, not Downloads) and run `dictum-gui.exe` from there.

### Linux (x86-64)

Pick one:

- **AppImage:** download `dictum-<version>-linux-x64.AppImage`, then `chmod +x dictum-*.AppImage` and run it. Keep it in a fixed place under a fixed name: "Start at login" remembers that path. The command line is inside it too: `./dictum-<version>-linux-x64.AppImage dictum status`.
- **Tarball (per user):** unpack `dictum-<version>-linux-x64.tar.gz` and run `./dictum-<version>-linux-x64/install.sh`. It installs into `~/.local/lib/dictum`, links `dictum` and `dictum-gui` into `~/.local/bin`, and adds Dictum to the application menu. Run it again from a newer tarball to update.
- **Arch, CachyOS:** put the tarball next to `packaging/linux/aur/PKGBUILD` and run `makepkg -si`. It installs into `/opt/dictum`.

Requirements:

- **CPU:** a 64-bit CPU with AVX2 (Intel Haswell, AMD Zen or newer).
- **Hotkeys on X11:** glibc 2.38 or newer (Ubuntu 24.04, Fedora 39, current Arch).
- **Pasting the text:** `wl-clipboard` on Wayland, `xclip` or `xsel` on X11.
- **Wayland:** `xdg-desktop-portal` and your desktop's portal backend (KDE or GNOME). See "Wayland and X11" below.

## First run

On first start Dictum opens its setup window. It asks for:

- your languages;
- a speech model (it recommends one for your GPU and memory, and downloads it with a SHA-256 check);
- optional text clean-up through an AI server you already run;
- your own words;
- whether to start at login.

Then it shows the hotkey, `Ctrl+Alt+Space` unless you change it. Hold it, speak, release it: the text is pasted where your cursor is.

On a machine without a desktop, run `dictum init` in a terminal instead. It asks the same questions.

## Wayland and X11

- **X11:** hotkeys and pasting work without setup.
- **Wayland:** Dictum asks the desktop portal for the hotkey (GlobalShortcuts) and for permission to type (RemoteDesktop).
  - KDE Plasma asks you once in a dialog; allow both.
  - GNOME needs the GlobalShortcuts portal (GNOME 48 or newer). On older GNOME, bind a key in Settings → Keyboard to `dictum toggle`.
  - GNOME shows tray icons only with the "AppIndicator and KStatusNotifierItem Support" extension.
- **Check:** `dictum doctor` lists what works, and what to install or allow when something does not.

## The command line

| Command | What it does |
| --- | --- |
| `dictum init` | First-time setup in the terminal |
| `dictum daemon` | Runs the daemon in the foreground. `--install-autostart` starts it at login without the tray app |
| `dictum status` | Shows whether the daemon runs, its profiles and hotkeys |
| `dictum toggle` | Starts or stops a dictation (for a hotkey bound in your desktop settings) |
| `dictum doctor` | Checks microphones, models, GPU, hotkeys, pasting and secrets |
| `dictum vocab add "VS Code" "Notepad++"` | Teaches Dictum words it gets wrong |
| `dictum models list` | Shows installed and available speech models |

In the AppImage, put `dictum` after the file name: `./dictum-<version>-linux-x64.AppImage dictum doctor`.

On Windows, `dictum.exe` sits next to `dictum-gui.exe` and is not on PATH. After the MSI, run it by its full path in PowerShell:

```powershell
& "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" doctor
```

To type plain `dictum`, add `%LOCALAPPDATA%\Programs\Dictum` (or the folder you unzipped into) to `Path` under "Edit environment variables for your account" in the Start menu.

## Start at login

Turn it on in the setup window or in Settings → General. Dictum then starts its tray app at login, and the tray app starts the daemon. To run only the daemon (no desktop), use `dictum daemon --install-autostart`.

## Updating and uninstalling

- **Windows:**
  - To update, open the newer MSI. It quits Dictum, replaces it and starts it again.
  - To remove, use Settings → Apps → Installed apps → Dictum → Uninstall. That also removes the login item.
- **Linux tarball:**
  - To update, run `install.sh` from the newer tarball.
  - To remove, run `~/.local/lib/dictum/uninstall.sh`. It quits Dictum and removes the program, the menu entry and its login items.
- **AppImage:** delete the file. Turn off "Start at login" first.
- **PKGBUILD:** `sudo pacman -R dictum-bin`.

Uninstalling keeps your settings and downloaded models, so a reinstall picks up where you left off.

## Where Dictum keeps its files

| | Windows | Linux |
| --- | --- | --- |
| Settings (`config.json`, prompts) | `%APPDATA%\Dictum` | `~/.config/dictum` |
| Models, history, logs | `%LOCALAPPDATA%\Dictum` | `~/.local/share/dictum` |

API keys go to the system keyring: Windows Credential Manager, or the Secret Service (GNOME Keyring, KWallet) on Linux. To start completely fresh, quit Dictum and delete both folders.

## Troubleshooting

- **First step:** `dictum doctor` (on Windows, by its full path: see "The command line").
- **Logs:** in the `logs` folder under the models-and-history folder above. `dictum daemon --verbose` prints more.
- **Hotkey does nothing:**
  - Wayland: check that the portal permission was granted (KDE: System Settings → Shortcuts → Dictum);
  - X11: check that no other app holds the same keys.
- **Text is not pasted:** install `wl-clipboard` (Wayland) or `xclip` (X11). Pasting into apps that run as administrator does not work on Windows.

## Building from source

You need the .NET 10 SDK; for native binaries, also the platform's C toolchain (MSVC build tools on Windows, `clang` and `zlib1g-dev` on Linux).

```sh
dotnet build
dotnet test
dotnet publish src/Dictum.Cli -c Release -r linux-x64     # or win-x64; native AOT builds only for the OS it runs on
packaging/package.sh linux-x64 artifacts/packages        # the packages for this OS (the MSI needs Windows)
```

`packaging/tests/test-package.sh` builds and checks the packages for the current OS.
