# Update and remove

How you update or remove Dictum depends on how you installed it. Whichever way you choose, your settings, your downloaded models, your history and the API keys in the keyring stay: an update or a reinstall picks up where you left off. [Start fresh](#start-fresh) says how to delete them as well.

The desktop app tells you when a new version is out: **Update available: 0.8.0**, with that version, shows in the tray icon's menu and opens **Settings → About Dictum**, where you update. See [Check for updates](../reference/settings.md#check-for-updates).

Package names are shown here with `<version>` in place of the version. The commands set it in a variable first, `version=0.7.0`: put in the version you downloaded.

## How Dictum updates itself {#self-update}

When a newer version is out, **Settings → About Dictum** shows what to do with your copy of Dictum:

- **The Windows installer, the AppImage and the tarball**: **Update now · 29 MB**, with the size of the download. One click downloads the new version, verifies it, closes Dictum, installs the new version the way you installed Dictum, and starts it again. While it downloads, **Cancel** stops it. A dictation in progress finishes first. When the new version starts, a notification says "Dictum was updated to 0.8.0".
- **The zip, and a copy you unpacked or built yourself**: **Download the new version** opens the release's page. Update by hand, as below.
- **The Arch package**: the commands that build the new package, and **Copy the commands**. See [Arch package](#pkgbuild).

Before it installs anything, Dictum checks the download against the release's provenance attestation: a record, signed through Sigstore, that Dictum's release workflow on GitHub built this file from Dictum's source. A file that does not match is deleted, and nothing is installed. [Privacy](../privacy.md#network) lists what is downloaded and from where.

If the installer fails, the old version starts again, and About Dictum says "The update to 0.8.0 did not finish." with **Open the log**. See [Troubleshooting](../troubleshooting.md#update-check).

### From the command line {#command-line}

`dictum update` checks for a newer version, says what it would do, and asks:

```text
$ dictum update
Dictum 0.7.0 → 0.8.0 is available. https://github.com/Sigil-build/dictum/releases/tag/v0.8.0
Installed as: AppImage (/home/you/Applications/Dictum.AppImage)
Dictum will download dictum-0.8.0-linux-x64.AppImage (38 MB), verify it, replace /home/you/Applications/Dictum.AppImage and restart Dictum.
Continue? [y/N]:
```

It downloads and verifies the new version, then hands the install over and exits, so that it does not stand in the installer's way: "Installing 0.8.0 now; Dictum starts again when it is done." with the path of the installer's log. What ran before starts again: the desktop app, or else the daemon, or else nothing. The next `dictum update` says how the install ended.

- `--yes` (or `-y`) skips the question. Without a terminal, as in a script, `dictum update` does nothing unless you pass it.
- `--check` only checks: exit code 0 when Dictum is up to date, 6 when an update is available.
- Exit code 5 means the download failed, and 7 that it could not be verified, so nothing was installed.
- For the zip and the Arch package, it prints the link or the commands, and changes nothing.

On Windows, run `dictum` by its full path: see [The command line](../getting-started.md#command-line). All options are in the [command reference](../reference/cli.md#update).

## Windows installer {#msi}

**To update**, click **Update now** on About Dictum, or run `dictum update`. Dictum downloads the newer `dictum-<version>-win-x64.msi` and runs it. Windows Installer quits Dictum, replaces it and starts it again.

By hand, open the newer `dictum-<version>-win-x64.msi` yourself. It does the same.

The installer does not put an older version over a newer one: it says "A newer version of Dictum is already installed." To go back to an older version, remove Dictum first, then install the older one.

**To remove:**

1. Open Windows Settings → **Apps** → **Installed apps**.
2. Find **Dictum**, click **…** next to it, and click **Uninstall**.

The uninstaller quits Dictum, and removes the programs, the Start menu entry and the login item.

## Windows zip {#zip}

Dictum does not update a copy from the zip itself: **Download the new version** on About Dictum opens the release's page.

**To update:**

1. Quit Dictum: **Quit Dictum** in the tray menu.
2. Extract the newer `dictum-<version>-win-x64.zip`, and copy the files from its `dictum-<version>-win-x64` folder into your Dictum folder, for example `C:\Tools\Dictum`. Let them replace the old files.
3. Start `dictum-gui.exe` again.

Keep the same folder: the login item points at it.

**To remove:**

1. Turn off **Start Dictum at login** in Settings → **General**, if it is on.
2. Quit Dictum: **Quit Dictum** in the tray menu.
3. Delete the folder.

## AppImage {#appimage}

These steps assume the AppImage lives at `~/Applications/Dictum.AppImage`, as [Getting started](../getting-started.md#install) suggests.

**To update**, click **Update now** on About Dictum, or run `~/Applications/Dictum.AppImage dictum update`. Dictum downloads the new AppImage into the same folder, puts it in place of the old one under the same name, and starts it. Under the same name, start at login keeps working. If the AppImage is a link, the file it points to is replaced. An AppImage in a folder you cannot write to is updated by hand, as below.

**By hand:**

1. Quit Dictum: **Quit Dictum** in the tray menu.
2. Put the new file in place of the old one, under the same name, and make it executable:

    ```sh
    version=0.7.0
    mv ~/Downloads/dictum-$version-linux-x64.AppImage ~/Applications/Dictum.AppImage
    chmod +x ~/Applications/Dictum.AppImage
    ```

3. Start it again.

Under the same name, start at login keeps working. If you keep each version under its own name instead, turn **Start Dictum at login** in Settings → **General** off and on again after the first start of the new file, so that the login item points at it.

**To remove:**

1. Turn off **Start Dictum at login** in Settings → **General**, if it is on. If you made only the daemon start at login, run this instead:

    ```sh
    ~/Applications/Dictum.AppImage dictum daemon --uninstall-autostart
    ```

2. Quit Dictum: **Quit Dictum** in the tray menu.
3. Delete the AppImage.
4. On Wayland, also delete the hidden entry through which the desktop portal recognises Dictum:

    ```sh
    rm ~/.local/share/applications/dev.dictum.Dictum.desktop
    ```

## Tarball with install.sh {#tarball}

**To update**, click **Update now** on About Dictum, or run `dictum update`. Dictum downloads the newer tarball, unpacks it into its [updates folder](../reference/files.md#data-folder), runs its `install.sh`, and starts Dictum again. A tarball you unpacked and run without `install.sh` is updated by hand, like the zip.

**By hand**, unpack the newer tarball and run its `install.sh`, as for the first install:

```sh
version=0.7.0
cd ~/Downloads
tar -xzf dictum-$version-linux-x64.tar.gz
./dictum-$version-linux-x64/install.sh
```

`install.sh` asks the running Dictum to quit, then replaces `~/.local/lib/dictum` in one step. The login item points into that folder, so it keeps working. Start Dictum again from the application menu.

**To remove**, run:

```sh
~/.local/lib/dictum/uninstall.sh
```

It quits Dictum and removes:

- the programs in `~/.local/lib/dictum`;
- the links `dictum` and `dictum-gui` in `~/.local/bin`;
- the menu entry and the icons;
- the login items and the portal's entry that start this copy of Dictum.

At the end, it prints where your settings and models are, which it keeps.

## Arch package {#pkgbuild}

Dictum does not update a package that pacman installed. About Dictum and `dictum update` show the commands that build the new one, and **Copy the commands** puts them on the clipboard:

```sh
mkdir -p ~/dictum-0.8.0 && cd ~/dictum-0.8.0
curl -LO https://github.com/Sigil-build/dictum/releases/download/v0.8.0/PKGBUILD
makepkg -si
```

**To update**, build the newer release's package the way you built the first one: run these commands, or put its `PKGBUILD` and `dictum-<version>-linux-x64.tar.gz` in one folder and run `makepkg -si` there. `makepkg` downloads the tarball the `PKGBUILD` names, if it is not there, and checks its SHA-256. pacman replaces the installed `dictum-bin`. Then quit Dictum with **Quit Dictum** in the tray menu and start it again, so that the new version runs.

**To remove:**

1. Turn off **Start Dictum at login** in Settings → **General**, if it is on. pacman does not touch the files in your home folder, and the login item is one of them.
2. Quit Dictum: **Quit Dictum** in the tray menu.
3. Remove the package:

    ```sh
    sudo pacman -R dictum-bin
    ```

4. On Wayland, also delete `~/.local/share/applications/dev.dictum.Dictum.desktop`, the hidden entry through which the desktop portal recognises Dictum.

## What removing keeps {#kept}

No uninstaller deletes these, so a reinstall finds your setup as you left it, with no wizard and no download:

| What | Windows | Linux |
| --- | --- | --- |
| Settings: `config.json` and your prompts | `%APPDATA%\Dictum` | `~/.config/dictum` |
| Models, history and logs | `%LOCALAPPDATA%\Dictum` | `~/.local/share/dictum` |
| API keys | Credential Manager | GNOME Keyring or KWallet |

[Where Dictum keeps its files](../reference/files.md) describes each of them.

## Start fresh {#start-fresh}

**To set Dictum up again and keep the models**, open Settings → **General** → **Setup wizard** and click **Run setup again**. The wizard starts from your current choices.

Or quit Dictum and delete only `config.json`, whose path `dictum config path` prints. The next start opens the wizard as on the first run, and models that are already on your computer are not downloaded again.

**To delete everything:**

1. Delete the API keys you stored, while the config still names them. Each `"apiKey": "secret:<name>"` in `config.json` is one key. List them:

    === "Windows 11"

        ```powershell
        Select-String '"secret:' "$env:APPDATA\Dictum\config.json"
        ```

    === "Linux"

        ```sh
        grep '"secret:' ~/.config/dictum/config.json
        ```

    Then delete each one by the name after `secret:`, for example as below. On Windows, run `dictum` by its full path: see [The command line](../getting-started.md#command-line).

    ```sh
    dictum secret delete dictum-processor-llm
    ```

2. Quit Dictum: **Quit Dictum** in the tray menu.
3. Delete both folders:

    === "Windows 11"

        ```powershell
        Remove-Item -Recurse -Force "$env:APPDATA\Dictum", "$env:LOCALAPPDATA\Dictum"
        ```

        This leaves the programs alone: the installer keeps them in another folder, `%LOCALAPPDATA%\Programs\Dictum`.

    === "Linux"

        ```sh
        rm -rf ~/.config/dictum ~/.local/share/dictum
        ```

4. Start Dictum. It opens the setup wizard as on the first run, and downloads the speech model again.
