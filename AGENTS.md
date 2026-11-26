# SSH Manager — заметки для ИИ-агентов

Менеджер SSH- и RDP-подключений для Windows (WinForms, `src/`), Linux (GTK 3 + VTE, `linux/`) и macOS (SwiftUI, `macos/`).

## Навигация по коду
- Граф кода: `graphify-out/graph.json`. Если папки нет или код сильно поменялся — `graphify update .` (без модели и сети).
- Перед поиском по коду: `graphify query "<вопрос>"`, затем читать только найденные места. `graphify explain "<класс>"`, `graphify path "<A>" "<B>"`.
- Единая спецификация интерфейса всех платформ: `docs/ui-spec.md`.
- План добавления VNC: `docs/vnc-plan.md`.

## Сборка и проверки
- Windows: `./build.ps1 -AppOnly -OutputDirectory build/check`, проверки — `tests/windows/README.md`.
- Linux: тесты `python3 -m unittest discover -s linux/tests` в образе `tests/linux-ui/Dockerfile`; встроенный RDP — `sh tests/linux-ui/run-rdp-check.sh`.
- macOS: собирается только в GitHub Actions (`build-release.yml`, задание `macos`).
- Тестовые серверы: `tests/xrdp/` (RDP и SSH, учётная запись `tester`).

## Правила
- Коммиты без строки `Co-Authored-By` и без упоминания ассистента.
- Новые подписи интерфейса сначала в `docs/ui-spec.md`, потом в код всех трёх платформ.
- Файлы с кириллицей и обратными слешами править через редактор или Python-скрипт, не через bash heredoc.
- Релиз — только по прямому решению владельца; процесс описан в `docs/vnc-plan.md`, Фаза 8.
