# Техническая основа нового Windows-меню

Дата: 21 октября 2026. Статус: исследование API, не спецификация продукта. В рабочей папке на момент исследования не было файлов и принятой структуры заметок.

## Краткий вывод

Для первого Windows-прототипа разумно проверить **C#/.NET**: `NotifyIcon` уже предоставляет значок и меню в области уведомлений, `RegisterHotKey` дает системную горячую клавишу, WinUI 3 предоставляет платформу интерфейса редактора. Это выбор по объему готовых API, а не измеренное сравнение производительности или надежности. Rust тоже технически возможен через `windows-rs`, но работа с Win32/COM остается явной частью проекта. [NotifyIcon](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon), [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey), [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/), [windows-rs](https://github.com/microsoft/windows-rs/blob/master/docs/crates/windows.md).

## Tray, меню, редактор

- Windows предоставляет область уведомлений через Shell API; иконка имеет сообщения обратного вызова. В .NET `NotifyIcon` инкапсулирует значок, события и контекстное меню. Это подходит для постоянно работающего пользовательского процесса. [Windows notification area](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area), [NotifyIcon](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon).
- `RegisterHotKey` регистрирует сочетание на уровне системы и доставляет `WM_HOTKEY` в окно или очередь потока. Регистрация может отказать при конфликте сочетаний; это нужно показать в UI. [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey).
- WinUI 3 является Windows UI платформой для настольных приложений. Выбор между ним и WPF для **компактного редактора** остается решением после макета и прототипа, а не следствием наличия tray API. [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/).
- Rust имеет официальные проекции Win32, COM и WinRT в `windows-rs`; `windows` дает типизированные привязки, `windows-sys` — низкоуровневый FFI. Это подтверждает осуществимость, но не гарантирует меньшую сложность реализации редактора. [windows-rs](https://github.com/microsoft/windows-rs/blob/master/docs/crates/windows.md).

## JSON и границы процессов

Предлагаемая схема, которую следует проверить прототипом: один процесс владеет конфигурацией, хранит валидированный снимок в памяти и атомарно публикует новую версию после успешной проверки. Редактор и MCP обращаются к этому процессу через один интерфейс команд; прямое параллельное редактирование файла ими создает риск потерянных обновлений. Здесь это архитектурная рекомендация, а не свойство Windows API. `FileSystemWatcher` может терять события при переполнении буфера, поэтому внешний ремонт файла должен иметь явную команду «перезагрузить» и повторную полную проверку. [FileSystemWatcher](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.internalbuffersize).

Для локального редактора подходит именованный канал: .NET содержит `NamedPipeServerStream`, Windows позволяет назначать права доступа к каналу. ACL нужно ограничить текущим пользователем/сеансом, поскольку умолчания могут дать доступ иным учетным записям. [NamedPipeServerStream](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream), [Named pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights). Версионированные команды вида `get_snapshot`, `apply_patch(expected_revision)`, `validate`, `show_menu` сохранят единые правила для UI, MCP и будущего внешнего API; это проектное предложение.

## MCP

Официальный C# SDK поддерживает stdio для локального дочернего процесса и Streamable HTTP для сетевого сервера. Если основной процесс уже висит в трее, отдельный **stdio MCP bridge** может подключаться к нему через локальный канал. Альтернатива — HTTP endpoint в tray-процессе; выбор зависит от клиента и модели доступа. [MCP C# transports](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html), [MCP transports specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports).

Предлагаемые узкие инструменты: чтение дерева, получение элемента, проверка изменения, создание/изменение/перемещение/удаление с `expected_revision`. Инструмент запуска действия стоит выделить отдельно от изменения конфигурации: он может выполнять программы и передавать им выделенный текст или пути. Это проектное ограничение полномочий, которое нужно закрепить при формировании требований. Для HTTP требуется отдельное решение об аутентификации и доступе; в локальном варианте именованный канал сам по себе защищает лишь доступ к соединению, а не полномочия разных команд. [ASP.NET Core named pipes security](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-namedpipes).

## Контекст действий

- **Файлы Проводника.** `ShellFolderView.SelectedItems` возвращает выбранные объекты представления. Это точка для прототипа при активном окне Explorer; не следует обещать, что она дает выбор в произвольной программе или файловом диалоге. Shell extension — отдельный механизм интеграции, требующий регистрации обработчика. [SelectedItems](https://learn.microsoft.com/en-us/windows/win32/shell/shellfolderview-selecteditems), [Shell extension handlers](https://learn.microsoft.com/en-us/windows/win32/shell/reg-shell-exts).
- **Выделенный текст.** UI Automation `TextPattern.GetSelection` предоставляет диапазоны выделения, когда контрол поддерживает TextPattern. Нужен прототип на целевых приложениях; универсальной гарантии для любого поля ввода из этого API не следует. [TextPattern](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationtextpattern).
- **Буфер обмена.** `GetClipboardData` читает существующее содержимое по формату. Он не равен текущему выделению и документация прямо требует считать данные недоверенными. Автоматическое `Ctrl+C` меняет состояние пользователя и зависит от фокуса. [Clipboard API](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard), [GetClipboardData](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboarddata).
- **Приложения с повышенными правами.** `SendInput` ограничен UIPI: обычный процесс может отправлять ввод только процессам того же или более низкого уровня целостности. Обход через повышение прав всему меню не следует закладывать как базовое решение. [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).

Практическое следствие: контекст должен содержать значения вместе с источником и статусом доступности, например `selection.files`, `selection.text`, `clipboard.text`, `active_window`; действие обязано явно реагировать на отсутствие переменной. Имена здесь предварительные, их следует уточнить с пользователем.

## Шаблоны действий

Для C# есть Scriban с контролем доступных объектов и методов; для Rust — MiniJinja с подстановкой значений. Оба решения намного шире простой замены переменных, поэтому начальная версия может ограничиться `{{ selection.text }}` и небольшим набором явно разрешенных преобразований. [Scriban](https://github.com/scriban/scriban/blob/master/readme.md), [MiniJinja](https://docs.rs/minijinja/latest/minijinja/).

Рендеринг строки команды сам по себе не решает экранирование аргументов. Лучше хранить **исполняемый файл, массив аргументов и рабочую папку раздельно**, а каждый аргумент шаблонизировать отдельно; запуск через shell включать явно. Это рекомендация для дизайна модели действий, требующая проверки на реальных примерах пользователя.

## Что выяснить до требований

1. Какие 5–10 действий QAP используются ежедневно и из каких приложений они вызываются?
2. Нужен ли выбор файлов только из Explorer, также из Open/Save диалогов или из других файловых менеджеров?
3. Нужен ли текст непосредственно из выделения или достаточно явно скопированного текста в буфере?
4. Какие действия агенту разрешено запускать, а какие только редактировать?
5. Нужно ли изменение JSON вне редактора, и как показывать конфликт двух изменений?
6. Какая минимальная глубина меню подтверждается текущим набором меню? Ограничение в 5 уровней пока гипотеза UX.
7. Какие сценарии будущих аппаратных ланчеров нужны: показать меню, вызвать действие по ID или менять конфигурацию?

## Минимальный технический эксперимент

Собрать прототип с tray, одной горячей клавишей, JSON из нескольких пунктов и отдельной компактной панелью редактора. Проверить `SelectedItems` и `TextPattern.GetSelection` на списке целевых приложений, а также поведение при недоступном контексте и конфликте двух правок. После этого фиксировать формат контекста, контракт MCP и выбор UI платформы.
