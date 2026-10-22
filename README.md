# XMLTV Guide Cleaner (Jellyfin plugin)

Some XMLTV sources — Channels DVR's guide feed in particular — bake a movie's release year
into its title (`<title>The Wizard of Oz (1939)</title>`) while also providing the year
separately (`<date>19390825</date>`). Jellyfin reads `<date>` as the production year and
appends it again when naming recordings, giving `The Wizard of Oz (1939) (1939)` for both
the recording title and its folder.

This plugin sits between the upstream guide and Jellyfin. It serves a cleaned copy at
`/XmltvCleaner/guide.xml`, with a trailing `(YYYY)` removed from a programme's title
**only when YYYY matches that programme's `<date>` year**. Everything else passes
through unchanged. On a 14-day Channels DVR guide (~30k programmes, 28 MB) that is
~2,200 movie titles, cleaned in under a second.

It also copies each programme's Gracenote/TMS id from `<episode-num system="tms">`, which
Jellyfin ignores, into `<episode-num system="dd_progid">`, which it reads. Jellyfin then
has a real, cross-channel episode id (its ShowId) instead of a fallback hashed from the
episode number alone. That fallback makes S1E5 and S3E5 of a show look like the same
episode, so "skip episodes already in library" cancels one of them. The Episode History
plugin uses the same id to match guide entries to what was recorded and watched.

Target: Jellyfin 12.0.x (.NET 10).

## How it works

- Jellyfin's XMLTV provider fetches `/XmltvCleaner/guide.xml` like any other XMLTV URL.
  The plugin fetches the configured upstream URL, cleans it, and returns it.
- The last good cleaned copy is kept at `<jellyfin data>/xmltv-cleaner/guide.xml`. If
  the upstream is down, times out, or returns an empty guide, that copy is served
  instead. This matters because Jellyfin doesn't check the HTTP status of an XMLTV
  download, so an error page would otherwise be cached as the guide.
- Requests within *Minimum minutes between upstream fetches* (default 10) reuse the
  cached copy. The guide endpoint is anonymous, because Jellyfin's downloader sends no
  credentials. The upstream URL comes only from plugin settings, so the endpoint can't
  be used to proxy anything else.
- Programme IDs are unaffected (Jellyfin builds them from channel + start time), so
  existing scheduled recordings keep working.

## Install

### From the JellyDVR plugin repository (no build needed)

1. Dashboard → Plugins → **Repositories** → add (one list for all the JellyDVR plugins):
   ```
   https://raw.githubusercontent.com/mackandall/jdvr-client/main/plugins/manifest.json
   ```
2. Dashboard → Plugins → **Catalog** → **XMLTV Guide Cleaner** → Install, then restart Jellyfin.

### Or build from source

```bash
dotnet build -c Release
sudo ./install.sh   # copies DLL + meta.json + icon.png into the plugin dir, restarts Jellyfin
```

## Setup

1. Dashboard → Plugins → XMLTV Guide Cleaner: paste the current upstream XMLTV URL,
   Save, then click **Fetch and clean now** to confirm it works.
2. Dashboard → Live TV → edit the XMLTV guide provider: replace its URL with the one
   shown on the plugin page, e.g. `http://localhost:8096/XmltvCleaner/guide.xml`.
3. Run Scheduled Tasks → **Refresh Guide**.

Recordings made before the switch keep their duplicated names. Only future recordings
are affected.

## License

GPL-2.0-or-later: you can redistribute and change it under the terms of the GNU General Public
License, version 2 or (at your option) any later version. See [LICENSE](LICENSE).

The official source and releases are at <https://github.com/mackandall/jellyfin-plugin-xmltv-cleaner>.
