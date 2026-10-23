# Запуск и визуальные проверки

- Открывай m3nuxa и тестовые окна на втором мониторе, если он подключён. Перед проверкой определи расположение экранов и перенеси окна в рабочую область второго монитора; проверь положение после запуска. Если доступен только один экран, используй его и сообщи об этом пользователю.
- После запуска обычного редактора для проверки выполни `./tools/Move-TestWindow.ps1`; для другого процесса передай `-ProcessName`. Автоматическое окно `m3nuxa.Editor.Checks` само выбирает дополнительный экран.

# Документация

Документы проекта находятся в `docs/`. Читай нужные материалы перед соответствующими изменениями:

- Термины и модель предметной области — [CONTEXT.md](docs/CONTEXT.md).
- Функциональность и критерии приёмки — [REQUIREMENTS.md](docs/REQUIREMENTS.md).
- Обоснования продуктовых решений и история согласования — [PRODUCT_DISCOVERY.md](docs/PRODUCT_DISCOVERY.md) и [REQUIREMENTS_REVIEW.md](docs/REQUIREMENTS_REVIEW.md).
- Выбор библиотек автоматизации — [research-dotnet-automation-libraries.md](docs/research-dotnet-automation-libraries.md) и [research-libreautomate.md](docs/research-libreautomate.md).
- Интеграции с Windows и границы процессов — [research-windows-menu-platform.md](docs/research-windows-menu-platform.md).
- Наборы иконок — [ICON_PACKS.md](docs/ICON_PACKS.md).
- Снимки прошлых визуальных проверок — `docs/screenshots/`; они не подтверждают поведение текущей версии.

Материалы согласования и исследований описывают решения на дату написания. Текущую реализацию проверяй по исходникам; команды запуска приведены в [README.md](README.md).
