# Проверки Windows-версии

Скрипты загружают собранный `SshManager.exe` через `Assembly.LoadFrom` и вызывают его внутренние классы через reflection. Запускать в **Windows PowerShell 5.1** (`powershell.exe`, не `pwsh`): приложение собрано под .NET Framework.

Сначала соберите приложение:

```powershell
./build.ps1 -AppOnly -OutputDirectory build/check
```

| Скрипт | Что проверяет | Запуск |
|---|---|---|
| `stage2-logic.ps1` | Модель без интерфейса: `tabs.xml` (избранное, недавние, команды, настройки, RDP «Дополнительно»), запись `ProxyJump` и пробросов в `config`, быстрое подключение, импорт mRemoteNG/MobaXterm, `ssh-keygen` | `powershell -NoProfile -ExecutionPolicy Bypass -File tests/windows/stage2-logic.ps1 -Exe build/check/SshManager.exe` |
| `render.ps1` | Снимки окон и диалогов в PNG, с имитацией DPI и тёмной темы | `powershell -NoProfile -ExecutionPolicy Bypass -File tests/windows/render.ps1 -Exe build/check/SshManager.exe -Out build/shots -Scale 1.5 -Scenes 'main,sessions,dialogs,sftp,rdpwindow,stage2' [-Dark]` |
| `montage.ps1` | Склеивает несколько PNG в один лист для просмотра | `powershell -NoProfile -File tests/windows/montage.ps1 -Out build/sheet.png -Width 2000 a.png b.png …` |
| `live-ssh.ps1` | Настоящий SSH к стенду `tests/xrdp` (порт 2223): копирование ключа, ProxyJump, LocalForward, команды, автопереподключение | см. ниже |
| `live-rdp.ps1` | Встроенный RDP к стенду `tests/xrdp` (порт 3390) с папками и принтерами | `powershell -NoProfile -STA -ExecutionPolicy Bypass -File tests/windows/live-rdp.ps1 -Exe build/check/SshManager.exe` |
| `tight-files.ps1` | Согласование файлового расширения TightVNC 2, загрузка и скачивание без SSH, отказ сервера и обрыв файлового канала. Требует Python 3.8+ | `powershell -NoProfile -ExecutionPolicy Bypass -File tests/windows/tight-files.ps1 -Exe build/check/SshManager.exe` |
| `live-vnc.ps1` | VNC через TigerVNC напрямую и через SSH, изменение разрешения и восстановление после рестарта | `powershell -NoProfile -STA -ExecutionPolicy Bypass -File tests/windows/live-vnc.ps1 -Exe build/check/SshManager.exe` |

Для VNC запустите [стенд TigerVNC](../vnc/README.md). Скрипт перезапускает тестовый контейнер `sshm-vnc` и добавляет в него временный открытый SSH-ключ.

Стенд для живых проверок (только тестовая учётная запись `tester`):

```sh
docker build -t sshm-xrdp-test tests/xrdp
docker run -d --name sshm-xrdp -p 3390:3389 -p 2223:22 sshm-xrdp-test
```

WSL останавливает Docker примерно через минуту после выхода последней команды `wsl.exe`. На время проверок держите в фоне `wsl.exe -e sleep 5400`.

`live-ssh.ps1` запускает `ssh` в псевдоконсоли. Если у самого PowerShell есть консоль, вывод `ssh` уйдёт в неё, а не в приложение, поэтому скрипт сам отключается от консоли, а запускать его нужно скрыто:

```powershell
Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/windows/live-ssh.ps1','-Exe','build/check/SshManager.exe' -RedirectStandardOutput build/live-ssh.out -Wait -WindowStyle Hidden
Get-Content build/live-ssh.out
```

Каждый скрипт печатает строки `ok` / `FAIL` и в конце `failures: N`.
