# Hand Mirror

A tiny Windows tray app that pops up your webcam in a small always-on-top window — for quickly checking your hair, lighting, or background before a meeting. Inspired by the macOS app [Hand Mirror](https://handmirror.app/).

## Features

- Lives in the system tray; left-click toggles the preview window.
- Top-center placement on the primary monitor.
- Window auto-resizes to match the camera's aspect ratio.
- Mirrored horizontally so it behaves like a real mirror.
- Rounded corners, draggable, always on top.
- "Start with Windows" toggle in the tray menu (registers under `HKCU\...\Run`).
- Closing the window fully releases the camera (LED off).

## Install

Download `HandMirrorSetup.exe` from the [latest release](../../releases) and run it. The installer is self-contained — no .NET runtime required.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish -c Release -r win-x64 --self-contained false
```

To build the installer ([Inno Setup 6](https://jrsoftware.org/isinfo.php) required):

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishDir=bin\Release\net10.0-windows10.0.19041.0\win-x64\publish-selfcontained\
"C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer\HandMirror.iss
```

## License

MIT
