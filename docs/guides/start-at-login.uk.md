# Запуск під час входу

Якщо запуск під час входу ввімкнено, програма Dictum для робочого столу запускається, коли ви входите в систему. Вона працює в треї, і на екрані нічого не відкривається. Програма запускає демон, а той у фоні завантажує модель мовлення, тож перше диктування не повільніше за інші. Доки ви не натиснете гарячу клавішу, Dictum не навантажує GPU.

## У майстрі налаштування {#in-the-wizard}

Крок 5 майстра налаштування питає **Запускати Dictum під час входу?**

- **Так, запускати під час входу** (з позначкою **рекомендовано**) вибрано під час першого налаштування.
- **Ні, я запускатиму його вручну** нічого не змінює у вашому вході.

Dictum записує запуск під час входу наприкінці майстра, після того як збереже налаштування. Якщо це не вдасться, останній крок покаже «Не вдалося налаштувати запуск під час входу: …» з причиною, а решта налаштувань лишиться. Там, де Dictum не може запускатися під час входу, крок каже «Запуск під час входу недоступний у цій системі».

## У Налаштуваннях {#in-settings}

1. Відкрийте **Налаштування** → **Загальні**.
2. Увімкніть або вимкніть **Запускати Dictum під час входу в систему**.

Зміна діє одразу. Якщо Dictum не може записати запуск, під перемикачем з'явиться причина: «Не вдалося змінити автозапуск: …». Решту сторінки **Загальні** описано в [довіднику налаштувань](../reference/settings.md#general).

Перемикач показує **Увімк.**, лише коли зареєстровано програму для робочого столу. Якщо зареєстровано тільки демон (див. [Лише демон](#daemon-only)), перемикач показує **Вимк.**, а якщо його ввімкнути, запис демона заміниться записом програми для робочого столу.

## Що саме реєструє Dictum {#what-it-registers}

=== "Windows 11"

    Dictum додає значення `Dictum` у розділ автозапуску вашого користувача, `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Дані значення — повний шлях до `dictum-gui.exe`. Для цього не потрібні права адміністратора, і це стосується лише вашого облікового запису.

    Щоб побачити його, виконайте в PowerShell або командному рядку:

    ```powershell
    reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Dictum
    ```

    ```text
    HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
        Dictum    REG_SZ    "C:\Users\you\AppData\Local\Programs\Dictum\dictum-gui.exe"
    ```

    Вимикайте запуск під час входу в Dictum, а не в списку автозавантаження Диспетчера завдань. Dictum читає лише розділ Run, тож коли Диспетчер завдань вимкне запис, перемикач Dictum усе одно показуватиме **Увімк.**

    Видалення Dictum у **Параметри** → **Програми** прибирає це значення. Якщо ви не встановлювали MSI, а розпакували Dictum з архіву, значення вказує на теку, куди ви його розпакували, тож не переносьте Dictum звідти.

=== "Linux"

    Dictum записує файл автозапуску XDG, `~/.config/autostart/dictum.desktop` (або в `$XDG_CONFIG_HOME/autostart`, якщо цю змінну задано). KDE Plasma, GNOME та інші робочі столи, що дотримуються стандарту автозапуску XDG, запускають його під час входу:

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

    `Exec` — повний шлях до `dictum-gui` або до AppImage, якщо Dictum запущено з нього. Щоб побачити запис:

    ```sh
    cat ~/.config/autostart/dictum.desktop
    ```

    `uninstall.sh` з архіву tar.gz його видаляє.

## Лише демон {#daemon-only}

На комп'ютері, де ви користуєтеся Dictum лише з термінала, без значка в треї, зареєструйте натомість демон:

```sh
dictum daemon --install-autostart
```

Команда виводить, куди записала запуск, а потім «The daemon will start at your next login.»

=== "Windows 11"

    Те саме значення `Dictum` у розділі Run тоді містить `"C:\Users\you\AppData\Local\Programs\Dictum\dictum.exe" daemon`. `dictum.exe` — консольна програма, тож під час входу Windows відкриває для неї вікно консолі. Якщо закрити це вікно, демон зупиниться.

=== "Linux"

    Dictum записує користувацький модуль systemd, `~/.config/systemd/user/dictum.service`, і вмикає його. Він стартує разом із графічним сеансом, який надають KDE Plasma 6 і GNOME, і перезапускається через 3 секунди після збою:

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

    Щоб запустити його одразу, не виходячи із системи, і подивитися його стан і вивід:

    ```sh
    systemctl --user start dictum.service
    systemctl --user status dictum.service
    journalctl --user -u dictum.service
    ```

Одночасно існує лише один запис запуску Dictum. `--install-autostart` замінює запис програми для робочого столу, а ввімкнений у Налаштуваннях перемикач **Запускати Dictum під час входу в систему** замінює запис демона.

Щоб Dictum більше не запускався під час входу, хоч який запис він має:

```sh
dictum daemon --uninstall-autostart
```

Команда виводить «Dictum no longer starts at login.»

`dictum init`, налаштування в терміналі, теж питає «Start Dictum when you log in?». Він реєструє програму для робочого столу, коли `dictum-gui` лежить поруч із `dictum`, а інакше — лише демон. `dictum init --autostart` відповідає «так», не питаючи.

!!! note
    У Windows інсталятор MSI не додає `dictum` до `PATH`. У PowerShell запускайте його за повним шляхом: `& "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" daemon --install-autostart`. В AppImage допишіть `dictum` після назви файла, як показано нижче.

## Незмінний шлях до AppImage {#appimage}

Запис запуску зберігає повний шлях до AppImage. Якщо файл перенести чи перейменувати або він лежить у теці, яку ви чистите, як-от «Завантаження», Dictum під час входу не запуститься. Дайте йому постійне місце й назву без номера версії:

1. Створіть для нього теку й перенесіть туди AppImage:

    ```sh
    mkdir -p ~/Applications
    mv ~/Downloads/dictum-<version>-linux-x64.AppImage ~/Applications/Dictum.AppImage
    chmod +x ~/Applications/Dictum.AppImage
    ```

2. Запустіть Dictum з нового місця:

    ```sh
    ~/Applications/Dictum.AppImage
    ```

3. Увімкніть запуск під час входу в майстрі налаштування або в **Налаштування** → **Загальні**. Тепер запис запускає `"/home/you/Applications/Dictum.AppImage"`.

Щоб зареєструвати з AppImage лише демон, допишіть `dictum` після назви файла:

```sh
~/Applications/Dictum.AppImage dictum daemon --install-autostart
```

Щоб оновитися, вийдіть із Dictum (**Вийти з Dictum** у меню трею), замініть `~/Applications/Dictum.AppImage` новим файлом під тією самою назвою, зробіть його виконуваним і запустіть знову. Подробиці — на сторінці [Оновлення й видалення](update-and-remove.md).

Якщо ви перенесли AppImage вже після того, як увімкнули запуск під час входу, запустіть його з нового місця, а потім вимкніть і знову ввімкніть **Запускати Dictum під час входу в систему** в **Налаштування** → **Загальні**. Dictum перезапише запис із новим шляхом.

`dictum init` попереджає, коли Dictum запущено з теки «Завантаження» чи з тимчасової теки: `warning: Dictum runs from /home/you/Downloads; login start breaks if that folder is moved or cleaned. Move Dictum to a permanent folder first.`

Перш ніж видалити AppImage, вимкніть запуск під час входу.
