# SoundFlow

**[Русский](#русский) · [English](#english)**

---

## Русский

Бесплатный системный эквалайзер для Windows. Работает поверх [Equalizer APO](https://sourceforge.net/projects/equalizerapo/): обрабатывает весь звук компьютера, без задержки и без окон «купите полную версию».

### Возможности

- **Предусиление** до +12 dB: громкий и плотный звук на малой громкости, удобно смотреть фильмы ночью. Ползунок двигаете только вы, программа его никогда не сбрасывает.
- **Графический 10-полосный эквалайзер** и **параметрический эквалайзер**:
  - тяните точку — меняются частота и усиление; стрелки по бокам или колесо — ширина полосы (Q); Shift — точная подстройка;
  - Ctrl + перетаскивание — послушать одну полосу отдельно;
  - двойной клик — добавить или удалить полосу; правый клик — сбросить в 0 dB; Shift + правый клик — тип фильтра;
  - типы: колокол, полки, срезы 12–48 dB/окт, режекция, полосовой; вырез до −60 dB;
  - живой спектроанализатор, отмена и повтор (Ctrl+Z / Ctrl+Y).
- **Каналы**: громкость, M (mute) и S (solo) для каждого канала устройства; любую полосу EQ можно назначить на отдельный канал.
- **Профили**: встроенные и свои.
- Запуск вместе с Windows (тихо, в трей), восстановление после сна и смены устройства.
- Интерфейс на русском и английском.

### Установка

Скачайте `SoundFlow-Setup.exe` в разделе [Releases](../../releases) и запустите. Установщик сам поставит Equalizer APO, если его ещё нет. В окне APO отметьте своё устройство воспроизведения и перезагрузите компьютер.

Удаление — через «Установку и удаление программ». Настройки SoundFlow убираются из Equalizer APO, сам APO остаётся.

### Сборка из исходников

Нужен [.NET 10 SDK](https://dotnet.microsoft.com/download) x64. Запустите `build-installer.cmd`, результат — `SoundFlow-Setup.exe`. Каждый коммит в `main` автоматически собирается в GitHub Actions; тег `vX.Y.Z` публикует релиз.

### Как это устроено

SoundFlow пишет настройки в `C:\Program Files\EqualizerAPO\config\soundflow.txt`, а `config.txt` подключает его строкой `Include: soundflow.txt`. Установщик даёт обычному пользователю право записи только в этот файл, поэтому программа работает без прав администратора. Настройки и журнал хранятся в `%AppData%\SoundFlow`.

| Файл | Что делает |
|---|---|
| `SoundFlow.App/MainForm.cs` | окно, профили, отмена, трей |
| `SoundFlow.App/ParametricGraph.cs` | интерактивный график EQ |
| `SoundFlow.App/Apo.cs` | конфиг Equalizer APO, расчёт фильтров |
| `SoundFlow.App/Audio.cs` | устройство по умолчанию, спектроанализатор |
| `SoundFlow.App/Settings.cs` | настройки, журнал, языки |
| `SoundFlow.Setup/Program.cs` | установщик |

---

## English

A free system-wide equalizer for Windows. It runs on top of [Equalizer APO](https://sourceforge.net/projects/equalizerapo/): it processes all of the computer's sound, with no latency and no "buy the full version" pop-ups.

### Features

- **Preamp** up to +12 dB: loud, dense sound at low volume, handy for watching films at night. Only you move the slider; the app never resets it.
- **Graphic 10-band EQ** and **parametric EQ**:
  - drag a dot to change frequency and gain; side arrows or the wheel change the width (Q); Shift gives fine control;
  - Ctrl + drag lets you listen to a band alone;
  - double-click adds or removes a band; right-click resets to 0 dB; Shift + right-click picks the filter type;
  - types: bell, shelves, cuts 12–48 dB/oct, notch, band-pass; cuts down to −60 dB;
  - live spectrum analyzer, undo and redo (Ctrl+Z / Ctrl+Y).
- **Channels**: level, M (mute) and S (solo) for each device channel; any EQ band can target a single channel.
- **Profiles**: built-in and your own.
- Starts with Windows (quietly, in the tray) and recovers after sleep and device changes.
- Russian and English UI.

### Install

Download `SoundFlow-Setup.exe` from [Releases](../../releases) and run it. The installer adds Equalizer APO if it is missing. In the APO window tick your playback device, then restart the computer.

To uninstall, use "Apps & features". SoundFlow's settings are removed from Equalizer APO; APO itself stays.

### Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) x64. Run `build-installer.cmd`; the result is `SoundFlow-Setup.exe`. Every commit to `main` is built by GitHub Actions; a `vX.Y.Z` tag publishes a release.

### How it works

SoundFlow writes its settings to `C:\Program Files\EqualizerAPO\config\soundflow.txt`, which `config.txt` pulls in with `Include: soundflow.txt`. The installer lets normal users write that one file only, so the app runs without admin rights. Settings and the log live in `%AppData%\SoundFlow`.

## License

SoundFlow is MIT-licensed, see [LICENSE](LICENSE). Bundled third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); Equalizer APO is GPL-2.0.
