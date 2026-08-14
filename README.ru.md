# SMTC Player

[![Скачать](https://img.shields.io/badge/Скачать-SMTC--Player.rmskin-2ea44f?style=for-the-badge)](https://github.com/konstantinstrejko-oss/smtc-player/releases/latest/download/SMTC-Player.rmskin)
[![Релиз](https://img.shields.io/github/v/release/konstantinstrejko-oss/smtc-player)](https://github.com/konstantinstrejko-oss/smtc-player/releases/latest)
[![Лицензия](https://img.shields.io/github/license/konstantinstrejko-oss/smtc-player)](LICENSE)

*[English version](README.md)*

Минималистичный виджет «сейчас играет» для [Rainmeter](https://www.rainmeter.net/).
Читает **Windows SMTC** (Global System Media Transport Controls) — тот же
системный канал, из которого рисуется оверлей громкости с обложкой.

Поэтому он работает с плеерами, которые не поддерживает штатный плагин
`NowPlaying`: **Яндекс Музыка**, Spotify, браузеры, VLC — всё, что репортит в
системный медиа-оверлей.

![SMTC Player](docs/screenshot.png)

## Установка

1. **[Скачать SMTC-Player.rmskin](https://github.com/konstantinstrejko-oss/smtc-player/releases/latest/download/SMTC-Player.rmskin)**
2. Открыть файл двойным кликом — запустится установщик самого Rainmeter.
3. Нажать **Install**. Скин загрузится сам, настраивать ничего не нужно.

![Установщик](docs/installer.png)

Нужен Rainmeter 4.5+ и Windows 10 1809 или новее (в этой версии появился SMTC API).
Виджет перетаскивается мышью, размер меняется колесом прокрутки.

### Если Rainmeter ещё не установлен

Rainmeter — бесплатная программа с открытым исходным кодом, сам виджет без неё
не заработает:

- официальный сайт: **https://www.rainmeter.net/**
- исходники и релизы: **https://github.com/rainmeter/rainmeter/releases/latest**

Ставится обычным установщиком, версия по умолчанию подходит.

> **Windows может предупредить о вложенном `.exe`.** Это фоновый мост — он не
> подписан, потому что сертификат для подписи кода стоит денег. Исходный код на
> C# лежит в этом же репозитории (`@Resources/Bridge/src`) и собирается одной
> командой, так что бинарник можно скомпилировать самому и заменить.

## Что умеет

- Название, исполнитель и обложка из любого плеера, который виден системе
- Прогресс-бар, который реально движется (большинство плееров отдают позицию
  снимком, а не потоком — подробности в [заметках](docs/notes-ru.md))
- Предыдущий / пауза / следующий и перемотка кликом по линии прогресса
- Нет обложки — карточка схлопывается, а не оставляет пустое место
- Колесо мыши меняет масштаб
- Показывает то, что реально играет, а не то, что просто держит медиа-клавиши

## Как это устроено

Rainmeter не умеет обращаться к WinRT, поэтому между ними стоит прослойка:

```
smtc_bridge.exe                        Rainmeter
 ├─ читает Windows SMTC (WinRT)
 ├─ !SetVariable ──WM_COPYDATA──▶      Player.ini  (трек, исполнитель, позиция …)
 └─ cmd.inc      ◀──!WriteKeyValue──   клики по кнопкам
```

Мост — исполняемый файл на C# размером ~14 КБ, окна у него нет. Скин запускает
его при загрузке, мьютекс держит одну копию, и мост выходит сам примерно через
минуту после закрытия Rainmeter.

При первом запуске он копирует себя в `%LOCALAPPDATA%\RainmeterSMTC` и работает
оттуда: запущенный файл внутри папки скина нельзя переместить, а установщик
Rainmeter при каждом обновлении переносит эту папку в `@Backup`. Туда же
складываются обложки и лог.

## Удаление

Правый клик по скину → **Unload skin**, затем удалить папку `SMTC Player` из
каталога скинов Rainmeter. Мост завершится сам; остатки лежат в
`%LOCALAPPDATA%\RainmeterSMTC` и тоже удаляются.

## Настройки

`@Resources\Variables.inc`:

| Переменная | Что делает |
|------------|------------|
| `TextColor`, `ButtonColor`, `ButtonHover`, `DimAlpha` | цвета |
| `PreferApp` | привязать виджет к одному приложению, например `ru.yandex.desktop.music`. Пусто — следить за текущей системной сессией |

## Сборка из исходников

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Собирает мост и пакует `dist\SMTC-Player.rmskin`. Нужен только `csc.exe` из
.NET Framework 4 (есть в любой Windows) и `Windows.winmd` из Windows SDK.
`build.ps1 -SkipBridge` пересобирает пакет без перекомпиляции. Пуш тега `v*`
собирает пакет на CI и прикладывает его к релизу.

`tools\deploy.ps1` кладёт скин прямо в папку Rainmeter — удобно при разработке.

## Благодарности

- Визуальный язык следует набору **Mond** от [Connect-R](https://www.deviantart.com/connect-r);
  иконки здесь свои, рисуются скриптом `tools\make_icons.ps1`.
- Шрифты: [Quicksand](https://fonts.google.com/specimen/Quicksand) (SIL OFL),
  Aquatico от Andrew Herndon (бесплатен для личного и коммерческого использования).

## Лицензия

MIT — см. [LICENSE](LICENSE). У шрифтов свои лицензии.
