# Manikin

An updated version of the original **FlashBox**
preview tool for AdventureQuest Worlds item art.

Type a character name (or drop in your own `.swf` files) and you get the actual
Flash avatar rendered with real dyes, hair, armor and weapons, on any of the
game's backgrounds, at whatever size you want to check it at.

## Features

| | |
|---|---|
| **Character** | Load any existing character by name, or drop your own item SWFs onto the gear slots |
| **Wardrobe** | Search ~50,000 catalog items by slot, filter Free / Member / AC, and try them on (Keep or Revert) |
| **Backgrounds** | All game scenes plus a custom solid colour and hex input |
| **Size** | Independent scale for character, pet and background |
| **Dyes** | Full HSV picker for the six dye channels, plus a theme colour for the app itself |
| **Dagger mode** | Force dual-wield so you can check how a split weapon set sits in both hands |
| **Drag background** | Pan the scene instead of having it locked to the stage |
| **Emotes** | Every animation on the avatar's timeline, grouped and searchable |
| **Gear rail** | See what you're wearing at a glance, and hide any slot with one click |
| **Headless mode** | Scripted assertions against the renderer for testing changes |

## Requirements

- Windows 10/11
- [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework) (to build)
- Flash Player ActiveX registered (for the avatar preview)
- WebView2 Runtime (for the control panel)

## Build

```
msbuild Manikin.csproj /p:Configuration=Release /p:Platform=x86
```

The exe lands in `bin\Manikin.exe`.

## Run

1. Launch `bin\Manikin.exe`.
2. Type a character name and hit Load — or drag any `.swf` straight onto the
   preview to try it on.
3. Use the side panel for backgrounds, size, dyes, emotes, settings and logs.

## Disclaimer

This project is an unofficial, independent community project. It has **no
affiliation with, and is not endorsed by, Artix Entertainment** or **the original
FlashBox** and its authors.

AdventureQuest Worlds, along with its art, backgrounds and character data, belongs
to Artix Entertainment and is used here only for personal, non-commercial
previewing.
