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

Остановить и удалить: `docker rm -f sshm-vnc`.

WSL останавливает Docker примерно через минуту после выхода последней команды `wsl.exe`. На время проверок держите в фоне `wsl.exe -e sleep 5400`.
