# Тестовый RDP-сервер

Одноразовый сервер xrdp с рабочим столом XFCE и OpenSSH для проверки RDP- и SSH-функций SSH Manager. Учётная запись только тестовая: `tester` / `Rdp-Test-2026`.

## Запуск (Docker, в том числе в WSL)

```sh
docker build -t sshm-xrdp-test tests/xrdp
docker run -d --name sshm-xrdp -p 3390:3389 -p 2223:22 sshm-xrdp-test
```

RDP доступен на `127.0.0.1:3390`, SSH — на `127.0.0.1:2223` (из Windows через пересылку портов WSL). Текущее разрешение последнего сеанса:

```sh
docker exec -u tester sshm-xrdp remote-size
```

Остановить и удалить: `docker rm -f sshm-xrdp`.

## Проверка Windows-клиента

```powershell
powershell -STA -ExecutionPolicy Bypass -File tests\xrdp\windows-rdp-check.ps1 -Exe build\release\SshManager.exe
```

Скрипт подключается встроенным клиентом, меняет размер окна, включает и выключает полноэкранный режим и печатает разрешение сеанса на каждом шаге. Для профиля с фиксированным размером: `-FixedWidth 1024 -FixedHeight 768`.

Ожидаемый результат при мониторе 1920×1080: `1000 x 640` → `1200 x 760` → `1920 x 1080` → `1200 x 760` (для фиксированного профиля `1024 x 768` → `1920 x 1080` → `1024 x 768`).

xrdp не присылает уведомление о завершении входа, поэтому стенд также проверяет, что сеанс считается подключённым по событию соединения.
