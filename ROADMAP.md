# SoundFlow roadmap / План развития

## Next big step: mixer console / Следующий большой шаг: микшерный пульт

**Idea (author, 2026-11-15).** SoundFlow becomes a mixing console:

- **Input strips = programs that make sound** (browser, player, game, call app…). Each strip is labelled with the program name and icon.
- On every strip: **its own EQ** (graphic or parametric), gain, mute/solo, meters.
- **Routing like a desk:** any strip → buses/groups → any **master output**.
- **Masters = physical output devices** (Realtek, USB card, HDMI…), each with its own EQ, level and channel layout. A program can go to one speaker, to several speakers or to several devices at once.

**Идея (автор, 2026-11-15).** SoundFlow превращается в микшерный пульт:

- **Входные полосы = программы, которые издают звук** (браузер, плеер, игра, звонки…). На полосе — название и значок программы.
- На каждой полосе: **своя эквализация** (графическая или параметрическая), усиление, mute/solo, индикаторы уровня.
- **Маршрутизация как на пульте:** любая полоса → шины/группы → любой **мастер-выход**.
- **Мастера = физические устройства вывода** (Realtek, USB-карта, HDMI…), у каждого своя эквализация, громкость и раскладка каналов. Программу можно отправить в одну колонку, в несколько колонок или сразу на несколько устройств.

### How it can work (to verify before building) / Как это может работать (проверить до начала)

1. **Capturing one program:** Windows 10 2004+/11 can capture the audio of a single process (process loopback, `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`). No driver needed.
   **Захват одной программы:** Windows 10 2004+/11 умеет захватывать звук отдельного процесса (process loopback). Драйвер не нужен.
2. **Silencing the original:** the program still plays to its device directly. To process it, its output is moved to a silent endpoint (per-app device setting, the same mechanism as Windows "App volume and device preferences"; used by EarTrumpet). Check that process loopback still delivers the sound then.
   **Чтобы не звучал оригинал:** программа продолжает играть в своё устройство. Её выход переводится на «немое» устройство (настройка устройства для приложения, как в «Параметрах громкости приложений» Windows). Проверить, что захват процесса при этом продолжает отдавать звук.
3. **DSP inside SoundFlow:** per-strip and per-master EQ run in SoundFlow itself (biquads, same math as now), not in Equalizer APO; outputs use the clock-following resampler that routing already has.
   **Обработка внутри SoundFlow:** эквалайзеры полос и мастеров считаются в самом SoundFlow (те же фильтры, что сейчас), а не в Equalizer APO; выходы — через уже готовый ресемплер маршрутизации.
4. **Latency budget:** ~20–40 ms per path; programs that are not routed keep playing directly with zero latency.
   **Задержка:** ~20–40 мс на маршрут; программы, которые не трогаем, играют напрямую без задержки.

### UI sketch / Набросок интерфейса

- Desk view: vertical strips left → right, masters on the right; each strip — icon, name, EQ thumbnail (click to open the EQ), fader, M/S, output buttons/matrix.
- The current routing matrix becomes the "patch" view of the desk.

Status: **planned, not started.** / Статус: **в планах, не начато.**
