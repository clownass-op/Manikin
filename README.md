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
| **Wardrobe** | Search ~50,000 catalog items by slot, put them on in one click, or roll a whole random outfit |
| **Backgrounds** | All game scenes plus a custom solid colour and hex input; the Gradient scene takes the picked colour |
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

## Wardrobe catalog

The item catalog loads from the GitHub mirror first
(`https://raw.githubusercontent.com/clownass-op/Manikin-Catalog/main/catalog.json.gz`,
refreshed daily by a scheduled snapshot job), then from Supabase, then from the
local Supabase. `MANIKIN_CATALOG_URL` overrides the mirror URL. The mirror is the
normal path now: one ~1.6 MB gzipped request instead of 51 PostgREST pages, and
no Supabase egress.

The catalog otherwise uses Supabase with the publishable key compiled into
`Wardrobe.cs` (`Config.DefaultUrl` / `Config.DefaultKey`), so a clone needs no
key file. `MANIKIN_SUPABASE_URL` and `MANIKIN_SUPABASE_KEY` override it, as does
an optional `supabase.key` next to the exe (git-ignored; one `url=` line and one
`key=` line). No local `wardrobe.json` backup is used: the mirror replaced it,
and a stale local copy hid real outages.

A publishable key is **not** a read-only key: what it is allowed to do is decided
by grants and row level security on the table. Keep `swfs` read-only for the
`anon` role so a published key cannot be used to insert rows:

```sql
revoke insert, update, delete, truncate on public.swfs from anon, authenticated;
grant select on public.swfs to anon, authenticated;
alter table public.swfs enable row level security;
drop policy if exists swfs_read_only on public.swfs;
create policy swfs_read_only on public.swfs
  for select to anon, authenticated using (true);
```

## Catalog mirror

The catalog mirror lives in its own repo (`clownass-op/Manikin-Catalog`), built
by a small exporter kept out of this one: it dumps the `swfs` table to a single
gzipped file on a daily GitHub Actions schedule, so the catalog can be read from
`raw.githubusercontent.com` instead of spending Supabase egress. `Wardrobe`
reads it before PostgREST, so the mirror is what the app normally uses.

## CharPage loader store

Loading a character by name used to download its gear SWFs into the folder next
to the exe. They now live in the GitHub catalog repo under `swfs/<path>`, one
file per CDN path: the loader reads `raw.githubusercontent.com` first and only a
miss fetches from the game CDN. The bytes go straight to the Flash player, so a
character load writes nothing to the device.

Saving them needs a token - `MANIKIN_GITHUB_TOKEN`, or a `github.key` next to the
exe (git-ignored; one `token=` line, from a fine-grained token with Contents:
read and write on that repo). It is deliberately not compiled in:
a published exe must stay read-only, or anyone could push to the repo every copy
downloads from. Without a token, saves fall back to the Supabase `char_swfs`
table (`tools/char_swfs.sql`), which is still read as a second-chance cache.

## Catalog sync

Settings > Catalog diffs aqcloset.com against what we already have. With a token
it appends the missing items to `catalog.json.gz` in the same repo (one commit
per sync, plus one that refreshes the row count and sha256 in
`catalog.meta.json`); without one it inserts them into the Supabase `swfs` table.
The nightly snapshot unions both stores, so items added either way survive.

## Disclaimer

This project is an unofficial, independent community project. It has **no
affiliation with, and is not endorsed by, Artix Entertainment** or **the original
FlashBox** and its authors.

AdventureQuest Worlds, along with its art, backgrounds and character data, belongs
to Artix Entertainment and is used here only for personal, non-commercial
previewing.
