# Update and remove

How you update or remove Dictum depends on how you installed it. Whichever way you choose, your settings, your downloaded models, your history and the API keys in the keyring stay: an update or a reinstall picks up where you left off. [Start fresh](#start-fresh) says how to delete them as well.

The desktop app tells you when a new version is out: **Update available: 0.5.0**, with that version, shows in the tray icon's menu and opens its page on GitHub. See [Check for updates](../reference/settings.md#check-for-updates).

Package names are shown here with `<version>` in place of the version. The commands set it in a variable first, `version=0.5.1`: put in the version you downloaded.

## Windows installer {#msi}

**To update**, open the newer `dictum-<version>-win-x64.msi`. It quits Dictum, replaces it and starts it again.

The installer does not put an older version over a newer one: it says "A newer version of Dictum is already installed." To go back to an older version, remove Dictum first, then install the older one.

**To remove:**

1. Open Windows Settings → **Apps** → **Installed apps**.
2. Find **Dictum**, click **…** next to it, and click **Uninstall**.

The uninstaller quits Dictum, and removes the programs, the Start menu entry and the login item.

## Windows zip {#zip}

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

**To update:**

1. Quit Dictum: **Quit Dictum** in the tray menu.
2. Put the new file in place of the old one, under the same name, and make it executable:

    ```sh
    version=0.5.1
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

**To update**, unpack the newer tarball and run its `install.sh`, as for the first install:

```sh
version=0.5.1
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

**To update**, build the newer release's package the way you built the first one: put its `PKGBUILD` and `dictum-<version>-linux-x64.tar.gz` in one folder and run `makepkg -si` there. pacman replaces the installed `dictum-bin`. Then quit Dictum with **Quit Dictum** in the tray menu and start it again, so that the new version runs.

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
