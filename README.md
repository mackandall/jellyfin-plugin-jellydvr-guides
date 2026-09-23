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

## Build and install

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
