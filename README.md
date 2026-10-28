# Domestique

A notch-style overlay for indoor cycling on Windows. It sits on top of any video or app, connects to a Bluetooth
smart trainer and shows power, heart rate, cadence, speed, distance, elevation gain and ride time. It simulates
routes from GPX files and controls the trainer in ERG or simulation mode.

## Features

- HUD docked at the top of the screen like a notch, or floating anywhere
- Small notch mode with the values you choose
- Bluetooth FTMS smart trainers (tested with the Van Rysel D100) and heart rate straps
- ERG mode and route simulation with an adjustable share of the real grade
- Built-in routes and your own GPX routes, with map and gradient profile
- Rides are recorded automatically and exported as TCX (Strava, Garmin Connect, …); recovered after a crash
- Global, customizable keyboard shortcuts
- Demo mode without a trainer

## Requirements

- Windows 10 (version 2004 / build 19041) or later with Bluetooth LE
- A smart trainer with the Fitness Machine Service (FTMS); a heart rate strap is optional
- To build: the .NET 10 SDK

## Build and run

```
dotnet build -c Release
dotnet run --project src/Domestique.App
```

Start with `--demo` to try it without a trainer. Click the overlay once, then press H to see the demo keys.

Single executable including the .NET runtime:

```
dotnet publish src/Domestique.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Run the tests with `dotnet test`.

## Shortcuts

All shortcuts work globally and can be changed in Settings → Shortcuts.

| Action                  | Default         |
|-------------------------|-----------------|
| ERG +10 W / −10 W       | Ctrl+Alt+PgUp / PgDn |
| ERG on / off            | Ctrl+Alt+Space  |
| Click-through on / off  | Ctrl+Alt+C      |
| Small notch on / off    | Ctrl+Alt+N      |
| Map on / off            | Ctrl+Alt+R      |
| Settings                | Ctrl+Alt+S      |

## Files

| What            | Where                                   |
|-----------------|-----------------------------------------|
| Settings        | `%APPDATA%\Domestique\settings.json`     |
| Route library   | `%APPDATA%\Domestique\Routes`            |
| Rides (CSV, TCX)| `Documents\Domestique`                   |
| Log             | `%LOCALAPPDATA%\Domestique\raw.log`      |

## Project structure

| Project                   | Contents                                                        |
|---------------------------|-----------------------------------------------------------------|
| `src/Domestique.Core`     | FTMS protocol, trainer control, physics, routes, recording (no UI) |
| `src/Domestique.Ble`      | Windows Bluetooth LE for trainer and heart rate strap           |
| `src/Domestique.App`      | WPF overlay                                                     |
| `tests/Domestique.Tests`  | xUnit tests for the core                                        |

## License

MIT, see [LICENSE](LICENSE). Parts of the animation code come from Coucou (MIT) and the numbers use the
Archivo Black font (SIL Open Font License); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
