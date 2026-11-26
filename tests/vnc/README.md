# Тестовый VNC-сервер

Одноразовый сервер TigerVNC с рабочим столом XFCE и OpenSSH для проверки VNC-функций SSH Manager. Учётные данные только тестовые: VNC-пароль `VncTest1`, SSH `tester` / `Vnc-Test-2026`.

## Запуск (Docker, в том числе в WSL)

```sh
docker build -t sshm-vnc-test tests/vnc
docker run -d --name sshm-vnc -p 5901:5901 -p 2224:22 sshm-vnc-test
```

VNC доступен на `127.0.0.1:5901`, SSH — на `127.0.0.1:2224`. Сервер без пароля: добавьте `-e SECURITY=None`.

Текущий размер экрана сервера:

```sh
docker exec -u tester sshm-vnc sh -c 'DISPLAY=:1 xrandr --current | head -1'
```

Проверка туннеля: VNC-профиль `127.0.0.1:5901` с «Через SSH-сервер» = псевдоним SSH на `127.0.0.1:2224` (внутри контейнера VNC слушает и на `127.0.0.1`).

Автоматический прогон Linux запускает собственный контейнер и проверяет вход, изменение размера рабочего стола, переподключение после рестарта и SSH-туннель.

```sh
sh tests/linux-ui/run-vnc-check.sh
```

Для Windows используйте `tests/windows/live-vnc.ps1` с уже запущенным стендом. Скрипт проверяет прямой вход и туннель за один прогон. [Команды проверки Windows](../windows/README.md)

Для проверки Unicode-буфера Windows запустите `tests/windows/live-vnc-clipboard.ps1 -Exe build/check/SshManager.exe` против стенда. Скрипт проверяет многострочный UTF-8 в обе стороны.

Остановить и удалить: `docker rm -f sshm-vnc`.

WSL останавливает Docker примерно через минуту после выхода последней команды `wsl.exe`. На время проверок держите в фоне `wsl.exe -e sleep 5400`.
