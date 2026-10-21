# Start at login

With start at login on, Dictum's desktop app starts when you log in. It sits in the tray, and nothing opens on screen. The app starts the daemon, which loads the speech model in the background, so your first dictation is not slower than the others. It uses no GPU until you hold the hotkey.

## In the setup wizard {#in-the-wizard}

Step 5 of the setup wizard asks **Start Dictum at login?**

- **Yes, start at login** (marked **recommended**) is chosen on a first setup.
- **No, I'll start it myself** leaves your login as it is.

Dictum writes the login entry at the end of the wizard, after it saves your settings. If that fails, the last step says "Start at login could not be set: …" with the reason, and the rest of the setup stands. Where Dictum cannot start at login, the step says "Starting at login is not available on this system."

## In Settings {#in-settings}

1. Open **Settings** → **General**.
2. Turn **Start Dictum at login** on or off.

The change applies at once. If Dictum cannot write the entry, the reason appears under the switch: "The login item could not be changed: …". The [Settings reference](../reference/settings.md#general) describes the rest of the **General** page.

The switch shows **On** only when the desktop app is registered. If only the daemon is registered (see [The daemon alone](#daemon-only)), it shows **Off**, and turning it on replaces the daemon's entry with the desktop app's.

## What Dictum registers {#what-it-registers}

=== "Windows 11"

    Dictum adds a value named `Dictum` under your user's Run key, `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Its data is the full path of `dictum-gui.exe`. This needs no administrator rights and affects only your account.

    To see it, run this in PowerShell or the Command Prompt:

    ```powershell
    reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Dictum
    ```

    ```text
    HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
        Dictum    REG_SZ    "C:\Users\you\AppData\Local\Programs\Dictum\dictum-gui.exe"
    ```

    Turn start at login off in Dictum, not in Task Manager's startup list. Dictum reads only the Run key, so after Task Manager disables the entry, Dictum's switch still shows **On**.

    Uninstalling Dictum in **Settings** → **Apps** removes the value. If you unzipped Dictum instead of installing the MSI, the value points into the folder you unzipped it to, so keep Dictum there.

=== "Linux"

    Dictum writes an XDG autostart entry, `~/.config/autostart/dictum.desktop` (under `$XDG_CONFIG_HOME/autostart` when that variable is set). KDE Plasma, GNOME and the other desktops that follow the XDG autostart standard start it when you log in:

    ```ini
    [Desktop Entry]
    Type=Application
    Name=Dictum
    Comment=Voice to text
    Exec="/home/you/.local/lib/dictum/dictum-gui"
    Icon=audio-input-microphone
    Terminal=false
    X-GNOME-Autostart-enabled=true
    ```

    `Exec` is the full path of `dictum-gui`, or of the AppImage when Dictum runs from one. To see the entry:

    ```sh
    cat ~/.config/autostart/dictum.desktop
    ```

    The tarball's `uninstall.sh` removes it.

## The daemon alone {#daemon-only}

On a computer where you use Dictum only from the terminal, without the tray app, register the daemon instead:

```sh
dictum daemon --install-autostart
```

It prints where it wrote the entry, then "The daemon will start at your next login."

=== "Windows 11"

    The same `Dictum` value in the Run key then holds `"C:\Users\you\AppData\Local\Programs\Dictum\dictum.exe" daemon`. `dictum.exe` is a console program, so Windows opens a console window for it at login. Closing that window stops the daemon.

=== "Linux"

    Dictum writes a systemd user unit, `~/.config/systemd/user/dictum.service`, and enables it. It starts with your graphical session, which KDE Plasma 6 and GNOME provide, and restarts 3 seconds after a crash:

    ```ini
    [Unit]
    Description=Dictum voice-to-text daemon
    PartOf=graphical-session.target
    After=graphical-session.target

    [Service]
    ExecStart="/home/you/.local/lib/dictum/dictum" daemon
    Restart=on-failure
    RestartSec=3

    [Install]
    WantedBy=graphical-session.target
    ```

    To start it now without logging out, and to see its state and output:

    ```sh
    systemctl --user start dictum.service
    systemctl --user status dictum.service
    journalctl --user -u dictum.service
    ```

Only one Dictum login entry exists at a time. `--install-autostart` replaces the desktop app's entry, and turning **Start Dictum at login** on in Settings replaces the daemon's.

To stop starting Dictum at login, whichever entry it has:

```sh
dictum daemon --uninstall-autostart
```

It prints "Dictum no longer starts at login."

`dictum init`, the setup in the terminal, also asks "Start Dictum when you log in?" It registers the desktop app when `dictum-gui` sits next to `dictum`, and the daemon alone otherwise. `dictum init --autostart` answers yes without asking.

!!! note
    On Windows, the MSI does not put `dictum` on `PATH`. In PowerShell, run it by its full path: `& "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" daemon --install-autostart`. In the AppImage, put `dictum` after the file name, as shown below.

## Keep an AppImage's path fixed {#appimage}

The login entry records the AppImage's full path. If you move or rename the file, or it sits in a folder you clean, such as Downloads, Dictum does not start at login. Give it a permanent place and a name without a version:

1. Make a folder for it and move the AppImage there:

    ```sh
    mkdir -p ~/Applications
    mv ~/Downloads/dictum-<version>-linux-x64.AppImage ~/Applications/Dictum.AppImage
    chmod +x ~/Applications/Dictum.AppImage
    ```

2. Start Dictum from its new place:

    ```sh
    ~/Applications/Dictum.AppImage
    ```

3. Turn on start at login, in the setup wizard or in **Settings** → **General**. The entry now runs `"/home/you/Applications/Dictum.AppImage"`.

To register the daemon alone from the AppImage, put `dictum` after the file name:

```sh
~/Applications/Dictum.AppImage dictum daemon --install-autostart
```

To update, quit Dictum (**Quit Dictum** in the tray menu), replace `~/Applications/Dictum.AppImage` with the new file under the same name, make it executable, and start it again. [Update and remove](update-and-remove.md) has the details.

If you moved the AppImage after turning start at login on, start it from its new place, then turn **Start Dictum at login** off and on again in **Settings** → **General**. Dictum writes the entry again with the new path.

`dictum init` warns you when Dictum runs from Downloads or a temporary folder: `warning: Dictum runs from /home/you/Downloads; login start breaks if that folder is moved or cleaned. Move Dictum to a permanent folder first.`

Before you delete the AppImage, turn start at login off.
