# .NET-библиотеки для первого прототипа Windows-меню

Проверено 21 октября 2026 года по первичным источникам. Первая версия включает глобальное сочетание для открытия меню, открытие папки в новом окне, запуск программы с параметрами, отправку одного сочетания клавиш и копирование/перемещение выделенных файлов Проводника. Библиотеки ниже сравниваются по назначению, а не по популярности или обещанной «отлаженности».

## Сначала разделить три задачи

| Задача | Нужный механизм | Важное ограничение |
| --- | --- | --- |
| Вызвать меню одним сочетанием | `RegisterHotKey` и обработка `WM_HOTKEY`; [документация Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) | Библиотеки отправки клавиш не регистрируют этим способом глобальное сочетание; регистрация может отказать при конфликте. |
| Отправить сочетание **в другое приложение** | `SendInput` либо обертка над ним; [документация Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) | `SendInput` подчиняется UIPI и не может вводить данные в приложение с более высоким уровнем целостности. |
| Получить выбранные объекты Explorer | Shell view API, например `ShellFolderView.SelectedItems`; [документация Microsoft](https://learn.microsoft.com/en-us/windows/win32/shell/shellfolderview-selecteditems) | Симуляция клавиатуры не является чтением выделения. Работу с текущим окном Explorer надо проверить прототипом. |

Для открытия пути .NET умеет запускать его через системную оболочку при `ProcessStartInfo.UseShellExecute=true` ([Microsoft](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-diagnostics-processstartinfo-useshellexecute)). Но официальный Shell API прямо предупреждает, что при открытии папки возможен повторно использованный экземпляр окна Explorer ([ShellExecute](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecutew)). Поэтому требование «всегда новое окно» нужно проверить на целевой Windows 11; нельзя считать его доказанным одной строкой `Process.Start(folder)`.

## Короткий список

| Библиотека | Проверенное назначение | Лицензия | Вывод для MVP |
| --- | --- | --- | --- |
| [H.InputSimulator](https://github.com/HavenDV/H.InputSimulator) | Обертка над `SendInput`: клавиатура, мышь, модифицированные сочетания; в README есть `ModifiedKeyStroke` и поддержка .NET 8+. | [Ms-PL](https://github.com/HavenDV/H.InputSimulator/blob/master/LICENSE.md). | Кандидат первой версии для действия «отправить одно сочетание клавиш». Для регистрации глобальной клавиши вызова меню и открытия папки не нужен. |
| [FlaUI](https://github.com/FlaUI/FlaUI) | Обертка над Microsoft UI Automation, плюс собственный модуль [Keyboard](https://github.com/FlaUI/FlaUI/blob/main/src/FlaUI.Core/Input/Keyboard.cs). | [MIT](https://github.com/FlaUI/FlaUI/blob/main/LICENSE.txt). | Кандидат для отдельного эксперимента с чтением UI сторонних приложений. Для простого меню избыточен; доступность элементов зависит от приложения. |
| [Vanara](https://github.com/dahall/Vanara) | Набор типизированных .NET P/Invoke для Windows; [User32 содержит `RegisterHotKey`](https://github.com/dahall/Vanara/blob/master/PInvoke/User32/readme.md). | [MIT](https://github.com/dahall/Vanara/blob/master/LICENSE). | Полезен, если не хочется держать собственное объявление Win32 API. Это обертка над API, а не готовый менеджер горячих клавиш или Explorer selection. |
| [Windows Input Simulator, исходный](https://github.com/michaelnoonan/inputsimulator) | C#-обертка над `SendInput` для клавиатуры и мыши; README демонстрирует сочетания. | [MIT](https://github.com/michaelnoonan/inputsimulator/blob/master/LICENSE). | Исторический вариант; для нового .NET прототипа сначала оценить H.InputSimulator. |

По GitHub API на дату проверки репозитории не архивированы; последняя отправка изменений в исходный InputSimulator указана в январе 2023 года, в H.InputSimulator — в октябре 2026, FlaUI — в августе 2026, Vanara — в октябре 2026. Это **сигнал активности репозитория**, а не доказательство качества, совместимости или даты опубликованного пакета. Источники: [InputSimulator](https://api.github.com/repos/michaelnoonan/inputsimulator), [H.InputSimulator](https://api.github.com/repos/HavenDV/H.InputSimulator), [FlaUI](https://api.github.com/repos/FlaUI/FlaUI), [Vanara](https://api.github.com/repos/dahall/Vanara). Для выбранной зависимости перед установкой отдельно проверить конкретную версию пакета в NuGet и протестировать на целевой версии Windows.

## Исключить AutoHotkey.Interop

[AutoHotkey.Interop](https://github.com/LucidMethod/AutoHotkey.Interop) прямо описан автором как обертка для AutoHotkey_H/AHKDLL и поставляет `AutoHotkey.dll`, в том числе как встроенный ресурс. Это сохраняет зависимость от AutoHotkey и противоречит исходному требованию. Наличие .NET API поверх AHK не меняет архитектуру. [Пакет NuGet](https://www.nuget.org/packages/AutoHotkey.Interop) ориентирован на .NET Framework 4.5; он не нужен в shortlist.

## Рекомендация

Для первого прототипа проверить H.InputSimulator как реализацию подтверждённого действия «отправить одно сочетание клавиш». Для вызова меню использовать системный `RegisterHotKey` через небольшую привязку либо Vanara; открытие папки реализовать через Shell/Process API с проверкой нового окна. Если потребуется чтение UI, проверить FlaUI отдельно. Для Explorer selection сначала проверить Shell API, а не строить обход на `Ctrl+C`. Это предварительные кандидаты, а не утверждённые зависимости.

Непроверенное поведение, которое нельзя обещать пользователю: гарантированно новое окно Explorer на всех настройках Windows 11; получение выделения из каждого приложения; отправка клавиш в повышенные процессы; отсутствие конфликтов глобального сочетания. Эти пункты требуют запуска прототипа на целевой системе. [ShellExecute](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecutew), [SelectedItems](https://learn.microsoft.com/en-us/windows/win32/shell/shellfolderview-selecteditems), [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput), [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey).
