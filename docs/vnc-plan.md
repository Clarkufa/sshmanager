# План: VNC в SSH Manager (Windows, Linux, macOS) + graphify

## Контекст
В SSH Manager есть SSH и RDP. Нужно добавить третий протокол — **VNC** — «так же, как RDP»: свой список профилей, вкладка протокола, сохранённый пароль, сеансы в разделе «Сеансы» с «Переподключить», «Весь экран», «В окне», «Закрыть», избранное, недавние, быстрое подключение, резервные копии, импорт. План рассчитан на исполнение недорогой моделью: каждый шаг называет файлы, классы, тексты и способ проверки. Делать по порядку, после каждой фазы — сборка, проверка, коммит.

Решения пользователя:
- **Windows** — свой встроенный VNC-клиент на C# (протокол RFB), внутри окна, как RDP.
- **Linux** — виджет gtk-vnc (`GtkVnc.Display`) внутри окна; работает и на X11, и на Wayland.
- **macOS** — TigerVNC Viewer, если установлен (пароль из Связки ключей, процесс отслеживается как сеанс), иначе встроенный «Общий экран» (`vnc://`).
- **SSH-туннель** — в профиле VNC поле «Через SSH-сервер» (псевдоним из каталога SSH); приложение само поднимает `ssh -N -L` и подключается к `127.0.0.1:<порт>`.
- **graphify** (https://github.com/Graphify-Labs/graphify, пакет PyPI `graphifyy`) ставится в проект первым шагом, чтобы модели быстрее находили код.

## Общие правила для исполнителя
- Коммиты **без** строки `Co-Authored-By` и без упоминания Claude. Ветка `claude/linux-parity` (или новая от `main`), не `main`.
- Работать последовательно, без параллельных сессий и субагентов.
- Правки в файлах с кириллицей и обратными слешами — через Edit или Python-скрипт, записанный инструментом Write. **Не** через bash heredoc: он схлопывает `\\` и ломает файлы.
- Сборка Windows: `./build.ps1 -AppOnly -OutputDirectory <папка>` (PowerShell). Тесты Linux: `docker run --rm -v $PWD:/src -w /src/linux sshm-linux-dev python3 -m unittest discover -s tests` (из WSL). macOS собирается только в CI: `gh workflow run build-release.yml --ref <ветка>`, затем `gh run watch <id>`.
- WSL гасит Docker примерно через минуту простоя: во время живых тестов держать в фоне `wsl.exe -e sleep 5400`.
- Перед `publish.ps1` удалить `linux/**/__pycache__` (скрипт требует чистое дерево).
- Тестовые пароли только из стендов в `tests/` (не настоящие).
- Единая спецификация интерфейса — `docs/ui-spec.md`; новые подписи сначала вписывать туда (§8, см. Фазу 1), потом в код.

---

## Фаза 0. graphify и проверочные скрипты — ВЫПОЛНЕНО
- graphify 0.9.107 установлен (`uv tool install graphifyy`), подключён к проекту (`graphify install --project`): правила в `CLAUDE.md`, навык в `.claude/skills/graphify/`, хуки в `.claude/settings.json`.
- Граф кода: `graphify update .` (без модели и сети, ~1 мин) → `graphify-out/` (в `.gitignore`). Если папки нет или код сильно поменялся — пересобрать этой командой.
- Как пользоваться: `graphify query "<вопрос>"` перед поиском по коду (например `graphify query "where are RDP passwords stored"`), `graphify explain "RdpDesktop"`, `graphify path "ConnectionsWindow" "RdpDesktop"`. Файлы читать только в местах, которые указал граф.
- Проверочные скрипты Windows лежат в `tests/windows/` (описание и команды — `tests/windows/README.md`). Новые скрипты писать по их образцу: загрузка `SshManager.exe` через `Assembly.LoadFrom`, внутренние типы через `Activator.CreateInstance` с флагами NonPublic, аргументы разворачивать из PSObject. Скрипты, где `ssh` работает в псевдоконсоли, вызывают `FreeConsole()` и запускаются через `Start-Process -WindowStyle Hidden -RedirectStandardOutput`.

## Фаза 1. Модель данных и спецификация (общая для всех платформ)
Дописать в `docs/ui-spec.md` раздел **§8 VNC** с этим содержимым:

| Что | Значение |
|---|---|
| Вкладка протокола | третья кнопка «VNC» после «SSH» и «RDP», значок Server |
| Заголовок каталога | «VNC-подключения» |
| Столбцы таблицы | «Название», «Адрес», «Пользователь», «Порт», «Вкладка», «Рабочий стол» (как у RDP) |
| Порт по умолчанию | 5900 |
| Пустой список | «Добавьте VNC-подключение.» |
| Окно профиля | «Новое VNC-подключение» / «Изменить VNC-подключение» |
| Поля | «Название», «Адрес», «Порт», «Пользователь» (необязательно, для серверов с логином), «Пароль» + подсказка «Необязательно. Хранится в …» / «Сохранён. Пустое поле — не менять.» + флажок «Удалить пароль»; «Через SSH-сервер» (выпадающий список: «Напрямую» + псевдонимы SSH); «Масштаб» (`По размеру окна` / `Исходный размер`); флажки «Только просмотр», «Общий буфер обмена», «Открывать на весь экран» |
| Подсказка к паролю | «Классический VNC-пароль учитывает только первые 8 символов.» |
| Меню строки | «Подключиться», «Новый сеанс», «Открыть в отдельном окне», «Изменить», «В избранное»/«Убрать из избранного», «Переместить во вкладку», «Удалить» |
| Подтверждение удаления | «Удалить VNC-подключение «Имя»?» |
| Сеанс: строка деталей | `VNC  ·  host:port  ·  вкладка  ·  статус` (+ `  ·  через alias` при туннеле) |
| Статусы | «подключение», «подключён», «отключён», «отключён (ошибка)» |
| Заглушка после отключения | «Сеанс завершён» / «Подключение прервано» + причина + «Переподключить», «Закрыть сеанс» |
| Ошибки | «Неверный пароль VNC.», «Сервер VNC не поддерживает доступный способ входа.», «Не удалось открыть SSH-туннель: …», «Сервер VNC закрыл соединение.» |
| Быстрое подключение | `vnc://[user@]host[:port]` |
| Ключи | избранное/недавние `vnc:<id>`; ключ сеанса `VNC:<id>` |

Хранение (только добавляемые поля, старые версии их пропускают):
- `tabs.xml` (Windows, macOS): элемент `<vnc id="32 hex" name="" host="" port="5900" user="" group="<id вкладки>" scale="fit|native" viewOnly="1" clipboard="1" fullScreen="1" via="<ssh alias>"/>`; атрибут корня `vncSort` (как `rdpSort`), у вкладки `vncSort`.
- `settings.json` (Linux): `"vnc": [{"id","name","host","port","user","group","scale","view_only","clipboard","fullscreen","via"}]`, сортировка `"sort"` с ключом протокола `vnc` (как `rdp`).
- Пароль: Windows Credential Manager `SSH Manager/VNC/<id>`; Linux Secret Service, ключ `sha256("vnc\n"+id)` (как `DesktopProfile.secret` в `linux/ssh_manager/rdp.py`); macOS Связка ключей, аккаунт `"vnc\n" + id`.
- Резервная копия: `app/passwords/vnc-<id>` (рядом с `rdp-<id>`), только в защищённом архиве.

Тест модели (Linux, `linux/tests/test_vnc.py`): разбор `settings.json` с `vnc`, неизвестные ключи не роняют разбор, валидация (имя, хост как у RDP, порт 1–65535, `scale` из двух значений, `via` — корректный псевдоним SSH или пусто).

## Фаза 2. Тестовый стенд VNC
Создать `tests/vnc/` по образцу `tests/xrdp/`:
- `Dockerfile`: `ubuntu:24.04`, пакеты `tigervnc-standalone-server tigervnc-tools xfce4 xfce4-terminal dbus-x11 openssh-server x11-xserver-utils`; пользователь `tester` с паролем `Vnc-Test-2026` (для SSH), VNC-пароль `VncTest1` (`printf 'VncTest1\n' | vncpasswd -f > /home/tester/.vnc/passwd`, права 600); `sshd` с `PasswordAuthentication yes`, `AllowTcpForwarding yes`. Запуск: `/usr/sbin/sshd && su tester -c "vncserver :1 -geometry 1280x800 -localhost no -SecurityTypes VncAuth -xstartup /usr/bin/startxfce4" && sleep infinity`. EXPOSE 5901 22.
- Второй вариант запуска (переменная `SECURITY=None`) — `-SecurityTypes None` для проверки входа без пароля.
- `README.md`: `docker build -t sshm-vnc-test tests/vnc`, `docker run -d --name sshm-vnc -p 5901:5901 -p 2224:22 sshm-vnc-test`. Размер экрана сервера: `docker exec -u tester sshm-vnc sh -c 'DISPLAY=:1 xrandr --current | head -1'`.
- Туннель проверяется так: профиль VNC `host=127.0.0.1 port=5901 via=<ssh alias на 127.0.0.1:2224>` (внутри контейнера сервер слушает и на 127.0.0.1).

---

## Фаза 3. Windows: встроенный VNC-клиент
Образец всех паттернов — RDP: `src/RdpProfile.cs`, `src/RdpDesktop.cs`, `src/RdpWindow.cs`, `src/RdpDialog.cs`, `RdpPasswords` в `src/SshPasswords.cs`. Новые файлы кладутся прямо в `src/` (`build.ps1` не заходит в подпапки): `src/VncClient.cs` (RFB), `src/VncKeys.cs`, `src/VncDesktop.cs`, `src/VncWindow.cs`, `src/VncDialog.cs` (+ `VncLogonDialog`), `src/VncTunnel.cs` (`SshTunnel`), `src/VncProfile.cs`, общий базовый класс `src/RemoteDesktopPanel.cs` (заглушка, статус, события — от него наследуют `RdpDesktop` и `VncDesktop`).

**Статус Windows: ВЫПОЛНЕНО** (Фазы 3 и 6 для Windows). Проверки: `tests/windows/live-vnc.ps1` (20 проверок: ZRLE/Hextile/Raw, пароль, смена размера, перезапуск сервера, туннель), `tests/windows/stage2-logic.ps1` (модель VNC, `vnc://`, импорт).

### 3.1 Протокол каталога: bool → enum
- Добавить `internal enum CatalogKind { Ssh, Rdp, Vnc }` (новый файл `src/CatalogKind.cs`).
- В `src/ConnectionsWindow.cs` заменить поле `showRdp` на `CatalogKind kind`, `SelectProtocol(bool)` → `SelectProtocol(CatalogKind)`, добавить кнопку `vncTab` = `Theme.Button("VNC", 86, glyph: Glyph.Server)` рядом с `sshTab`/`rdpTab`. Все места `if (showRdp)` переписать на `switch (kind)` (поиск: `showRdp`). Заголовок: «SSH-подключения» / «RDP-подключения» / «VNC-подключения».
- `Groups.Sort(id, bool rdp)` и `SetSort` → параметр `CatalogKind`; добавить `ServerSort VncSort` в `Groups` и `ServerGroup`, атрибут `vncSort` в `GroupFile.Parse`/`Update` (`src/Groups.cs`, по образцу `rdpSort`).
- `src/ServerPickerDialog.cs`: третья вкладка «VNC», `bool showRdp` → `CatalogKind`; в `Choice` добавить VNC-конструктор.
- Проверка: сборка, `tests/windows/render.ps1 -Scenes main` — три вкладки протокола, SSH/RDP работают как раньше.

### 3.2 Профиль и хранение
- `src/VncProfile.cs`: `internal sealed class VncProfile` с полями `Id, Name, Host, User, GroupId, Via` (string), `Port` (int, 5900), `Scale` (`"fit"`/`"native"`), `ViewOnly, Clipboard, FullScreen` (bool). Конструктор валидирует как `RdpProfile` (скопировать проверки `Name`, `Host` через `IPAddress.TryParse`/`Uri.CheckHostName`, порт). `Via` пусто или `ConfigDocument.IsAlias(via)`. Методы `Parse(XElement, string groupId)`, `ToXml()`, `InGroup(id)`, свойства `Address`, `Key => "VNC:" + Id`.
- `src/Groups.cs`: `public readonly List<VncProfile> Vnc = new …`; `SaveVnc(profile, editing)`, `MoveVnc(id, groupId)` (как `SaveDesktop`/`MoveDesktop`); разбор `root.Elements("vnc")` с проверкой повторов id; запись `latest.Vnc.Select(p => p.ToXml())`; `RemoveTab` переносит VNC-профили во «Без вкладки» (как Desktops). `Groups.Key(VncProfile p) => "vnc:" + p.Id`.
- `src/SshPasswords.cs`: `internal static class VncPasswords` — копия `RdpPasswords` с `Name(id) => "SSH Manager/VNC/" + id`, `BackupPrefix = SshPasswords.BackupPrefix + "vnc-"`.
- Резервные копии: в `src/BackupFiles.cs` (там, где `RdpPasswords.BackupPrefix + profile.Id`) добавить такой же цикл для `groups.Vnc`; в `src/BackupRestore.cs` — словарь `validVnc` и ветку `VncPasswords.IsBackupEntry`, по образцу `validDesktops`.
- Тест: расширить `tests/windows/stage2-logic.ps1` — `tabs.xml` с `<vnc …/>` читается, пишется обратно, неверный `via` отклоняется, `RemoveTab` переносит профиль.

### 3.3 RFB-клиент (новая папка `src/Vnc/`)
`src/VncClient.cs` — без UI, работает в фоновом потоке. Спецификация RFB: RFC 6143. Порядок байтов — big-endian.

1. **Подключение**: `TcpClient` к host:port, таймаут 15 с. `NetworkStream` + `BinaryReader`-обёртка с методами `ReadExact(n)`, `ReadU8/U16/U32/S32` (big-endian вручную).
2. **Версия**: прочитать 12 байт `"RFB xxx.yyy\n"`; ответить `"RFB 003.008\n"`, если сервер ≥ 3.8, иначе `"RFB 003.003\n"`.
3. **Безопасность (3.8)**: `U8 count`; если 0 — прочитать причину (`U32 len` + текст) и бросить `RfbException`. Выбор: если есть 1 (None) → 1; иначе если есть 2 (VNC Authentication) → 2, но если пароль не задан — бросить `RfbPasswordRequired` (UI показывает окно входа и подключается заново с паролем); иначе ошибка «Сервер VNC не поддерживает доступный способ входа.» (например, только Apple ARD тип 30 или VeNCrypt тип 19 — в первой версии не поддерживаются, упомянуть это в README). Отправить выбранный `U8`. (3.3: сервер сам присылает `U32 type`, логика та же.)
4. **VNC Authentication**: прочитать 16 байт challenge. Ключ: пароль в Latin-1, обрезать/дополнить нулями до 8 байт, **в каждом байте развернуть порядок битов** (bit 0 ↔ bit 7). Если `DES.IsWeakKey(key) || DES.IsSemiWeakKey(key)` — ошибка «Этот пароль VNC не поддерживается.». Зашифровать challenge `DESCryptoServiceProvider { Mode = ECB, Padding = None, Key = key }` (два блока по 8) и отправить 16 байт.
5. **SecurityResult**: `U32` 0 = ок; иначе (3.8) причина `U32 len` + текст → ошибка «Неверный пароль VNC.» (+ причина).
6. **ClientInit**: `U8 shared` (1, если профиль не «только просмотр» с монопольным режимом — по умолчанию всегда 1).
7. **ServerInit**: `U16 width, U16 height`, 16 байт PIXEL_FORMAT, `U32 nameLen`, имя.
8. **SetPixelFormat** (тип 0): 3 байта отступа, затем формат `bpp=32, depth=24, bigEndian=0, trueColor=1, redMax=255, greenMax=255, blueMax=255, redShift=16, greenShift=8, blueShift=0`, 3 байта отступа. Тогда пиксель = 4 байта little-endian `0x00RRGGBB` = BGRA в памяти — совпадает с `PixelFormat.Format32bppRgb`.
9. **SetEncodings** (тип 2): список `S32`: `16 (ZRLE)` — только после шага 3.6, `5 (Hextile)`, `1 (CopyRect)`, `0 (Raw)`, `-223 (DesktopSize)`, `-308 (ExtendedDesktopSize)`.
10. **Цикл**: отправить `FramebufferUpdateRequest` (тип 3, `incremental=0`, весь экран), затем читать сообщения сервера:
    - 0 FramebufferUpdate: 1 байт отступа, `U16 count`; для каждого прямоугольника `U16 x,y,w,h, S32 encoding`:
      - Raw: `w*h*4` байт → скопировать строки в кадр.
      - CopyRect: `U16 srcX, srcY` → скопировать область кадра (с учётом перекрытия: через временный буфер).
      - Hextile: плитки 16×16 слева направо, сверху вниз; `U8 mask`: бит 1 Raw (плитка сырыми пикселями, остальные биты игнорируются), бит 2 BackgroundSpecified (4 байта цвета фона), бит 4 ForegroundSpecified (4 байта), бит 8 AnySubrects (`U8 n` прямоугольников), бит 16 SubrectsColoured (у каждого подпрямоугольника свой цвет 4 байта). Подпрямоугольник: `U8 xy` (x = старшие 4 бита, y = младшие), `U8 wh` (w-1 старшие, h-1 младшие). Фон и передний цвет сохраняются между плитками одного прямоугольника.
      - DesktopSize (-223) / ExtendedDesktopSize (-308): новый размер = w×h, пересоздать кадр, событие `Resized`. Для -308: `U8 screens`, 3 байта отступа, затем `screens × 16` байт — пропустить.
      - Неизвестная кодировка → `RfbException("Сервер прислал неподдерживаемую кодировку …")`.
      - После всех прямоугольников: событие `Updated(Rectangle dirtyUnion)` и новый `FramebufferUpdateRequest` с `incremental=1`.
    - 1 SetColourMapEntries: прочитать и пропустить (у нас trueColor).
    - 2 Bell: игнорировать.
    - 3 ServerCutText: 3 байта отступа, `U32 len`, текст Latin-1 → событие `ClipboardReceived` (если включён «Общий буфер обмена»). Ограничить len 1 МБ.
11. **Ввод** (методы потокобезопасны, пишут под `lock (writeLock)`):
    - `SendKey(uint keysym, bool down)` — тип 4: `U8 down`, 2 байта отступа, `U32 keysym`.
    - `SendPointer(byte buttons, int x, int y)` — тип 5: `U8 mask, U16 x, U16 y`. Биты: 1 левая, 2 средняя, 4 правая, 8 колесо вверх, 16 колесо вниз (колесо = нажатие и отпускание).
    - `SendClipboard(string)` — тип 6: 3 байта отступа, `U32 len`, Latin-1 (символы вне Latin-1 заменить `?`).
    - `RequestDesktopSize(int w, int h)` — только если сервер прислал -308: тип 251 SetDesktopSize: 1 байт отступа, `U16 w, U16 h`, `U8 screens=1`, 1 байт отступа, экран `U32 id=0, U16 x=0, U16 y=0, U16 w, U16 h, U32 flags=0`.
    - В режиме «Только просмотр» ввод не отправляется.
12. **Кадр**: `Bitmap frame` формата `Format32bppRgb`; запись через `LockBits` только в фоновом потоке под `lock (frameLock)`; UI рисует под тем же замком.
13. **События** (вызываются в фоновом потоке; потребитель делает `BeginInvoke`): `Connected(string desktopName, Size size)`, `Updated(Rectangle)`, `Resized(Size)`, `ClipboardReceived(string)`, `Disconnected(Exception errorOrNull)`.
14. `Close()` закрывает сокет; поток завершается через `Disconnected(null)`.

### 3.4 Клавиши: WinForms → X11 keysym
`src/VncKeys.cs`, `static uint? FromKeyDown(Keys key)` для служебных клавиш:
Back 0xff08, Tab 0xff09, Enter 0xff0d, Escape 0xff1b, Insert 0xff63, Delete 0xffff, Home 0xff50, Left 0xff51, Up 0xff52, Right 0xff53, Down 0xff54, PageUp 0xff55, PageDown 0xff56, End 0xff57, F1–F12 = 0xffbe + n−1, ShiftKey 0xffe1, ControlKey 0xffe3, Menu (Alt) 0xffe9, LWin/RWin 0xffeb, CapsLock 0xffe5, NumLock 0xff7f, PrintScreen 0xff61, Pause 0xff13, цифровой блок NumPad0–9 = 0xffb0 + n, Multiply 0xffaa, Add 0xffab, Subtract 0xffad, Decimal 0xffae, Divide 0xffaf.
Печатные символы — из `KeyPress`: символ c ≤ 0xFF → keysym = c; иначе `0x01000000 | c`. При зажатом Ctrl (`KeyPress` даёт управляющий символ) отправлять букву/цифру из `KeyDown`: `Keys.A..Z` → 0x61..0x7a (строчная), `D0..D9` → 0x30..0x39. Каждое нажатие — пара down/up; модификаторы отправлять отдельно по `KeyDown`/`KeyUp`. Переопределить `IsInputKey` (стрелки, Tab) и `ProcessCmdKey`, чтобы Tab/стрелки шли на сервер.

### 3.5 Элемент `VncDesktop` (как `RdpDesktop`)
`src/VncDesktop.cs`: `internal sealed class VncDesktop : Panel`.
- Публичный интерфейс повторяет `RdpDesktop` (см. `src/RdpDesktop.cs`): `Connect(VncProfile, string password)`, `Reconnect()`, `Disconnect()`, `FullScreen()`, `IsOpen`, `IsConnected`, `Status`, `Profile`, события `Changed`, `CloseRequested`.
- Отрисовка: `DoubleBuffered`, в `OnPaint` под `frameLock` рисовать кадр. Масштаб «По размеру окна»: вписать с сохранением пропорций, по краям фон `NativeTerminalView.Background`, `InterpolationMode.HighQualityBilinear`. «Исходный размер»: `AutoScroll = true`, `AutoScrollMinSize = frame.Size`, рисовать со смещением `AutoScrollPosition`.
- Мышь: перевести координаты окна в координаты кадра (обратное масштабирование, ограничить границами), отправлять маску кнопок при `MouseDown/Up/Move`, колесо — по 1 нажатию на каждые 120 `Delta`.
- Клавиатура: см. 3.4. `TabStop = true`, фокус по щелчку.
- Буфер обмена: при получении фокуса, если включён `Clipboard` и текст в буфере изменился, `SendClipboard`; входящий `ClipboardReceived` → `Clipboard.SetText` в UI-потоке (с `try/catch ExternalException`).
- Если сервер поддерживает -308 и масштаб «По размеру окна» → после изменения размера (таймер 450 мс, как `ResizeDelay` в `RdpDesktop`) `RequestDesktopSize(ClientSize)` с учётом DPI.
- Заглушка после отключения: скопировать из `RdpDesktop` поля `overlay, overlayReason, overlayTitle, overlayDetail, overlayActions` и метод `FitOverlay` (или вынести их в общий `src/SessionOverlay.cs` и использовать в обоих классах — предпочтительно). Заголовок «Сеанс завершён» (сервер закрыл штатно) / «Подключение прервано» (ошибка), причина — текст исключения.
- Статусы: «подключение» → «подключён» → «отключён» / «отключён (ошибка)».
- Весь экран: создать `VncFullScreenForm` (без рамки, `WindowState = Maximized`, `TopMost`, на мониторе текущего окна), перенести в него этот же `VncDesktop` (`Parent = form`), вверху полупрозрачная панель с «Выйти из полноэкранного режима» (появляется при наведении на верхние 4 px); `Ctrl+Alt+Enter` — выход. При выходе вернуть элемент в прежний контейнер.
- Отдельное окно: `src/VncWindow.cs` по образцу `src/RdpWindow.cs` (`Open(VncProfile, string password, IWin32Window owner)`, верхняя строка с «Весь экран», «Переподключить», «Отключить»).
- Пароль не сохранён → подключаться без пароля; при `RfbPasswordRequired` (или «Неверный пароль VNC.») показать диалог «Вход: имя» (как `RdpLogonDialog.Resolve` в `src/RdpDialog.cs`, но поле «Пароль» и флажок «Сохранить пароль в Windows») и переподключиться с введённым паролем. «Отмена» → заглушка «Подключение прервано» с причиной «Нужен пароль VNC.».

### 3.6 ZRLE (после того как Raw/CopyRect/Hextile работают)
- zlib-поток один на всё соединение. В .NET Framework нет `ZLibStream`: в начале первого ZRLE-прямоугольника пропустить 2 байта заголовка zlib, дальше `DeflateStream` поверх своего `ChunkStream : Stream`, в который дописываются данные каждого прямоугольника (`U32 len` + данные). `ChunkStream.Read`: если данных нет — бросить `InvalidDataException("ZRLE: не хватает данных")`.
- Плитки 64×64. CPIXEL при нашем формате = 3 байта (B, G, R). Подкодировка `U8`: 0 — сырые CPIXEL; 1 — один цвет; 2–16 — палитра из N цветов, индексы упакованы (1 бит при N=2, 2 бита при 3–4, 4 бита при 5–16), каждая строка выровнена на байт; 128 — простой RLE (CPIXEL + длина: сумма байтов, пока байт = 255, плюс 1); 130–255 — палитра из (код−128) цветов, затем серии: индекс < 128 — одна точка, ≥ 128 — индекс−128 и длина как в RLE.
- После этого добавить 16 в начало списка `SetEncodings`.

### 3.7 Интеграция в каталог и сеансы
- `src/SessionsPage.cs`: в `SessionTarget` добавить `public readonly VncProfile Vnc;`, конструктор `SessionTarget(VncProfile, ServerGroup)`, `Protocol` → «VNC», `Key` → `profile.Key`. В `TerminalSession` поле `public readonly VncDesktop Vnc;`. Везде, где проверяется `Desktop != null` (кнопки «Переподключить», «В окне», «Весь экран», меню сеанса, `FitButtons`, `Activate`, `DetachActive`, `OnDesktopChanged`), обрабатывать `Vnc` так же. Общий интерфейс удобно задать как `interface IRemoteDesktop { … }`, который реализуют `RdpDesktop` и `VncDesktop`, и хранить одно поле `Remote`.
- `Open`: ветка для `target.Vnc` — пароль (`VncPasswords.Read` или диалог входа), затем туннель (Фаза 6), затем `OpenVnc` по образцу `OpenDesktop`.
- `src/ConnectionsWindow.cs`: `RefreshVnc` (по образцу `RefreshDesktops`), `EditVnc`/`DeleteVnc` (по образцу `EditDesktop`/`DeleteDesktop`, с переносом пароля при смене id не нужно — id постоянный), `ConnectVnc`, меню строки `ShowVncMenu` (пункты из §8), звезда избранного в `PaintGridCell` (ключ `Groups.Key(VncProfile)`), «Недавние» (`RecordRecent`).
- `src/VncDialog.cs` — окно профиля по образцу `RdpDialog` (поля из §8, рамки `Theme.Frame`, «Через SSH-сервер» — `ComboBox` с «Напрямую» и псевдонимами из текущего `ConfigDocument`).
- Быстрое подключение: `QuickTarget` (`src/Extras.cs`) понимает `vnc://` (порт по умолчанию 5900), `ConnectionsWindow.QuickConnect` создаёт временный `VncProfile` и предлагает «Сохранить подключение «host»?».
- Импорт (`src/Extras.cs` `Importers`): mRemoteNG `Protocol="VNC"`, MobaXterm тип 5 (проверить на реальном файле `.mxtsessions`; если тип другой — поправить). `FoundConnection` получает `Kind` вместо `bool Rdp`.
- Проверка 3.x: `tests/windows/stage2-logic.ps1` (модель, RFB-ключ DES на известном примере: пароль `password`, challenge из RFC 6143 тестовых векторов или из захвата стенда), `tests/windows/render.ps1` (каталог VNC, окно профиля, сеанс с заглушкой при 100/150/200% и тёмной теме), новый `tests/windows/live-vnc.ps1`: подключение к `sshm-vnc` (127.0.0.1:5901, `VncTest1`) → `IsConnected`, размер кадра 1280×800, снимок, щелчок и ввод `echo VNC-OK` в xfce4-terminal не обязателен; изменение размера окна при `scale=fit` меняет размер сервера (если Xvnc поддерживает -308 — TigerVNC поддерживает), отключение сервера (`docker restart sshm-vnc`) → заглушка «Подключение прервано», «Переподключить» восстанавливает.

## Фаза 4. Linux: gtk-vnc

Живой прогон на TigerVNC и 41 модульный тест прошли. Прямое подключение и SSH-туннель проверяются одним запуском.
- Зависимости: `linux/build.sh` — `Recommends:` добавить `gir1.2-gtk-vnc-2.0`; `linux/ssh-manager.spec` — `Recommends: gtk-vnc2`; `tests/linux-ui/Dockerfile` — пакет `gir1.2-gtk-vnc-2.0`. Без пакета при подключении сообщение «Для VNC установите пакет gir1.2-gtk-vnc-2.0 (Debian/Ubuntu) или gtk-vnc2 (Fedora).».
- Модель: новый `linux/ssh_manager/vnc.py` с `@dataclass(frozen=True) class VncProfile` (поля из Фазы 1, `validate()` по образцу `DesktopProfile.validate` в `rdp.py`, свойства `alias`, `address`, `secret` → `DesktopAccount(sha256("vnc\n"+id), user or "-", host, port)`). `config.py` `parse_settings`: ключ `"vnc"` (список, до 10000, неверные записи — ошибка, как `desktops`).
- Сеанс: `class VncSession(Session)` в `linux/ssh_manager/sessions.py` по образцу `RdpSession`:
  - `gi.require_version("GtkVnc", "2.0")`, `from gi.repository import GtkVnc` внутри `try` (нет пакета → сообщение выше).
  - `self.display = GtkVnc.Display()`; `set_scaling(scale == "fit")`, `set_keep_aspect_ratio(True)`, `set_read_only(view_only)`, `set_shared_flag(True)`, `set_pointer_grab(False)`, `set_keyboard_grab(True)` только в полноэкранном режиме.
  - Сигналы: `vnc-auth-credential` → для каждого запрошенного типа `GtkVnc.DisplayCredential.PASSWORD/USERNAME` вызвать `display.set_credential(...)`; `vnc-connected` → статус «подключение»; `vnc-initialized` → «подключён»; `vnc-auth-failure` → ошибка «Неверный пароль VNC.»; `vnc-error` → текст ошибки; `vnc-disconnected` → заглушка (страница `ended`, как в `RdpSession`); `vnc-server-cut-text` → буфер обмена (если включён), обратное направление — при фокусе `display.client_cut_text(text)`.
  - `start()`: `display.open_host(host, str(port))` (или `127.0.0.1` и локальный порт туннеля); `restart()` — `close()` и снова `start()`.
  - «Весь экран» и «В окне» — те же методы окна, что для `RdpSession` (`enter_full_screen`, `detach_session` в `app.py`); для «В окне» создать отдельное `Gtk.Window` и переподключиться в нём.
- Каталог/окно (`linux/ssh_manager/app.py`, `catalog.py`, `dialogs.py`): третья вкладка в `catalog.py` (`(("ssh","SSH","terminal"),("rdp","RDP","server"),("vnc","VNC","server"))`), `select_protocol("vnc")`, заголовок, список `vnc_profiles()`, меню строки, `VncDialog` по образцу `DesktopDialog`, избранное/недавние (`favorite_key` → `("vnc", id)`), `ServerPicker` (третья вкладка), `parse_quick` в `extras.py` (`vnc://`), импорт mRemoteNG/MobaXterm.
- Резервная копия (`linux/ssh_manager/backup.py`): `VNC_PREFIX = "vnc-"` рядом с `RDP_PREFIX`, цикл по `settings["vnc"]`.
- Тесты: `linux/tests/test_vnc.py` (модель, parse_quick, импорт); снимки `tests/linux-ui/screens.py` (каталог VNC, окно профиля); живая проверка `tests/linux-ui/vnc_embedded.py` + `run-vnc-check.sh` по образцу `rdp_embedded.py`/`run-rdp-check.sh` (подключение к контейнеру `sshm-vnc` по сети Docker, `VncTest1`): подключён, размер кадра совпадает с размером виджета при масштабе «По размеру окна», заглушка после `docker restart sshm-vnc`, «Переподключить». Код завершения ≠ 0 при любом провале.

## Фаза 5. macOS: TigerVNC или «Общий экран»

Модель, каталог, сеансы, резервные копии и SSH-туннель реализованы. Универсальный DMG собрался в GitHub Actions. Подключение на реальном Mac ещё предстоит проверить.
- `macos/Sources/Vnc.swift`: `struct VncProfile: Identifiable, Equatable` (поля Фазы 1), `validate()` по образцу `DesktopProfile` в `Rdp.swift`, `keychainAccount = "vnc\n" + id`, разбор/запись элемента `vnc` в `Settings.swift` (рядом с `rdp`, массив `settings.vnc`).
- Клиент: `static func tigerVNC() -> URL?` — искать `/Applications/TigerVNC Viewer*.app/Contents/MacOS/TigerVNC Viewer`, `/opt/homebrew/bin/vncviewer`, `/usr/local/bin/vncviewer`.
  - С TigerVNC: пароль записать во временный файл в формате VNC passwd (8 байт, зашифрованные DES ECB фиксированным ключом `[23, 82, 107, 6, 35, 78, 88, 7]`; через CommonCrypto `CCCrypt(kCCEncrypt, kCCAlgorithmDES, kCCOptionECBMode, …)`), права 0600, запуск `vncviewer -passwd <файл> [-FullScreen] [-ViewOnly] [-Shared] host::port` через `Process`; файл удалить через 10 с после запуска. Сеанс отслеживает процесс (статус «подключён» после 3 с жизни процесса, «отключён» при выходе, код выхода ≠ 0 → «Подключение прервано»).
  - Без TigerVNC: `NSWorkspace.shared.open(URL("vnc://" + user@ + host:port))` открывает «Общий экран»; пароль, если сохранён, на 60 с копируется в буфер обмена — повторить механизм Windows App из `macos/Sources/DesktopSession.swift`. Сеанс показывает «Рабочий стол открыт в приложении «Общий экран».» и не знает, когда тот закрыт (как с Windows App).
- `macos/Sources/VncSession.swift` по образцу `DesktopSession.swift` (`start(fullScreen:)`, `stop()`, `live`, `status`, `endedTitle`, `endedDetail`).
- `Manager.swift`: `ConnectionProtocol` добавить `case vnc = "VNC"`; `SessionEntry` — `case vnc(VncSession)` (все `switch` по `SessionEntry` в `Manager.swift` и `ManagerView.swift` дополнить); `connectVnc`, `saveVnc`, `removeVnc`, `moveVnc`, `vncEditor`; `Manager.key(VncProfile)` = `"vnc:" + id`; `recordRecent`; `quickConnect` понимает `vnc://`.
- `ManagerView.swift`: вкладка протокола (цикл `ConnectionProtocol.allCases` подхватит сам), строки таблицы в `ServerTable.rows`, меню `vncMenu`, `VncEditor` по образцу `DesktopEditor`, `SessionsPage` строки/кнопки/меню для `.vnc`, `ServerPicker` (вкладка VNC).
- Резервная копия (`Backups.swift`): `backupPasswordPath(_ profile: VncProfile) = passwordPrefix + "vnc-" + profile.id`, сбор и восстановление как для `DesktopProfile`.
- Проверка: только сборка в CI (`build-release.yml`, задание `macos`) без ошибок и предупреждений; в отчёте явно написать, что на Mac не запускалось.

## Фаза 6. SSH-туннель («Через SSH-сервер»)
Общая логика: найти свободный порт (`TcpListener(IPAddress.Loopback, 0)`, взять порт, закрыть), запустить `ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -L 127.0.0.1:<порт>:<host>:<port> <alias>`, ждать до 15 с, пока к `127.0.0.1:<порт>` не начнёт подключаться TCP (проверять каждые 200 мс); если процесс ssh завершился раньше — ошибка «Не удалось открыть SSH-туннель: <последние строки stderr>». Туннель живёт столько же, сколько VNC-сеанс; при «Закрыть сеанс» и при выходе из приложения процесс ssh завершается. «Переподключить» перезапускает и туннель.
- Windows: `src/VncTunnel.cs`. Запуск — как в `src/SftpClient.cs` (конструктор): `PasswordTarget.From(connection)`, `password?.Arguments`, `new SshAskPass(password)` + `askPass.SetEnvironment(...)`, `Terminal.SshExecutable()`, `CreateNoWindow = true`, stderr собирать в буфер. Если сохранённого пароля нет — добавить `-o BatchMode=yes` и в ошибке подсказать сохранить пароль SSH-сервера или использовать ключ.
- Linux: `linux/ssh_manager/vnc.py` `class SshTunnel` — `ssh_args(profile, config, account)` и `AskPass(account, password)` из `auth.py`, как в `transfer.py` (строка с `self.broker = AskPass(...)`), `subprocess.Popen` с `env=broker.environment(...)`.
- macOS: `SSHRequest(profile: sshProfile).arguments(noForwarding: true)` не подходит (там `ClearAllForwardings`) — собрать аргументы вручную: базовые опции из `SSHRequest.arguments` + `-N -o ExitOnForwardFailure=yes -L …`, окружение `SSHRequest.environment(prompt: true)`, чтобы работали сохранённый пароль, запрос пароля и подтверждение ключа сервера.
- Проверка: стенд `sshm-vnc` — SSH на 127.0.0.1:2224 (пароль `Vnc-Test-2026`, сохранить в приложении), VNC-профиль `host=127.0.0.1 port=5901 via=<alias>`; на Windows `tests/windows/live-vnc.ps1`, на Linux `tests/linux-ui/run-vnc-check.sh`. Оба сценария проверяют прямой вход и туннель за один прогон.

## Фаза 7. Документация
- `docs/ui-spec.md` §8 (Фаза 1) актуализировать по факту.
- `README.md`: раздел «Удалённый рабочий стол (VNC)» после RDP — как добавить, где хранится пароль, туннель через SSH, отличия платформ (Windows — встроенный клиент; Linux — gtk-vnc, пакет `gir1.2-gtk-vnc-2.0`/`gtk-vnc2`; macOS — TigerVNC Viewer или «Общий экран»).
- `docs/windows.md`, `linux/README.md`, `macos/README.md` — по абзацу; `tests/vnc/README.md`.

## Фаза 8. CI и релиз
- `.github/workflows/rdp-check.yml`: добавить задание `linux-vnc` (`sh tests/linux-ui/run-vnc-check.sh`, артефакт со снимками), пути `tests/vnc/**`. Для изменения workflow у токена `gh` должно быть право `workflow` (уже выдано).
- Релиз — только после явного «выпускай» пользователя: версии в `installer/app.manifest`, `src/AssemblyInfo.cs`, `linux/ssh_manager/__init__.py`, `macos/Resources/Info.plist` (+ `CFBundleVersion`); `gh workflow run build-release.yml`, `gh run download`; RPM через `git -c core.autocrlf=false archive` + `docker run fedora:44 … build/test-rpm-fedora.sh`; fast-forward `main`, `./publish.ps1 -NotesFile … -Python "%APPDATA%\uv\python\cpython-3.11.15-windows-x86_64-none\python.exe" -MacDmg … -LinuxDeb … -LinuxRpm … -LinuxArchive …`.

## Передача файлов напрямую через VNC

В Windows кнопка «Файлы» проверяет расширение TightVNC 2 в отдельном RFB-подключении и подтверждает доступ чтением корневого каталога. Если прямой доступ недоступен, файловое окно открывает SFTP через явно указанный «SSH-профиль для файлов». В Linux и macOS поддерживается этот вариант через SFTP. Расширения других VNC-серверов остаются в плане.

1. Проверять возможности конкретного сеанса при согласовании протокола или при открытии «Файлы». Версия RFB, имя рабочего стола и ОС удалённой машины не показывают, разрешена ли передача. Результат нужно обновлять при переподключении. Различать подтверждённую поддержку, отказ сервера и неизвестную возможность.
2. Для TightVNC проверять объявленные файловые команды, сопоставляя код, поставщика и сигнатуру. TightVNC 1.x и 2.x используют разные файловые расширения. Само наличие режима Tight не подтверждает передачу. [Спецификация Tight](https://github.com/rfbproto/rfbproto/blob/master/rfbproto.rst#tight-security-type) · [Версии TightVNC](https://www.tightvnc.com/whatsnew.php)
3. Для UltraVNC проверять разрешение и версию файлового протокола средствами его расширения. Запрос отправлять после согласования совместимости с этим расширением. [Определения протокола UltraVNC](https://github.com/ultravnc/UltraVNC/blob/main/rfb/rfbproto.h)
4. Включать передачу по VNC только для согласованного расширения, которое реализовано в клиенте. Если передача по VNC недоступна и настроен SSH-профиль, открывать SFTP. Для внешних клиентов показывать только возможности, которые SSH Manager может подтвердить. RealVNC документирует передачу между своими Viewer и Server с учётом лицензии и разрешений пользователя. [Передача файлов RealVNC](https://help.realvnc.com/hc/en-us/articles/360002250477-Transferring-Files-Between-Computers)
5. Проверять по каждому реализованному расширению чтение каталога, загрузку и скачивание. На стенде нужны случаи с разрешённой и запрещённой передачей, обычный VNC без файловых расширений и профиль с SFTP. При неизвестном ответе или отказе рабочий стол должен оставаться открыт.

## Ключевые файлы
- Windows: `src/ConnectionsWindow.cs`, `src/SessionsPage.cs`, `src/Groups.cs`, `src/SshPasswords.cs`, `src/ServerPickerDialog.cs`, `src/Extras.cs`, `src/BackupFiles.cs`, `src/BackupRestore.cs`, новые `src/CatalogKind.cs`, `src/VncProfile.cs`, `src/VncDialog.cs`, `src/VncClient.cs`, `src/VncKeys.cs`, `src/VncDesktop.cs`, `src/VncWindow.cs`, `src/VncTunnel.cs`, `src/RemoteDesktopPanel.cs`.
- Linux: `linux/ssh_manager/{app,catalog,dialogs,sessions,config,backup,extras}.py`, новый `vnc.py`; `linux/build.sh`, `linux/ssh-manager.spec`, `tests/linux-ui/Dockerfile`.
- macOS: `macos/Sources/{Manager,ManagerView,Settings,Backups,Extras}.swift`, новые `Vnc.swift`, `VncSession.swift`.
- Общее: `docs/ui-spec.md`, `tests/vnc/`, `tests/windows/`, `.github/workflows/rdp-check.yml`, `.claude/skills/graphify/`, `.gitignore`, `AGENTS.md`.

## Проверка (итог)
1. Windows: сборка; `tests/windows/stage2-logic.ps1` (0 ошибок); `tests/windows/render.ps1` при 1, 1.5, 2 и `-Dark` — VNC-каталог, окно профиля, сеанс; `tests/windows/live-vnc.ps1` против `sshm-vnc` напрямую и через SSH; старые `live-ssh.ps1` и `live-rdp.ps1` по-прежнему проходят (регрессия SSH/RDP).
2. Linux: `unittest` в `sshm-linux-dev` (все тесты, включая `test_vnc.py`); `run-vnc-check.sh` и `run-rdp-check.sh` — `PASS`; снимки `screens.py`.
3. macOS: CI `build-release.yml` — задание `macos` зелёное, без предупреждений.
4. CI `rdp-check.yml` — оба задания (`linux-rdp`, `linux-vnc`) зелёные.
5. Ручная проверка пользователем на реальном VNC-сервере (например, TigerVNC/RealVNC/macOS «Общий доступ к экрану») — перед релизом.
