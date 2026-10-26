# JellyDVR Guides (Jellyfin plugin)

Guide sources for Jellyfin's Live TV. Each source is an XMLTV guide or a Schedules Direct
lineup, served by this plugin as one guide at its own address on your server, for one
Jellyfin guide provider limited to the tuners it's for. Formerly *XMLTV Guide Cleaner*:
existing setups carry over (see [Upgrading from XMLTV Guide Cleaner](#upgrading-from-xmltv-guide-cleaner)).

Target: Jellyfin 12.0.x (.NET 10).

## Why

**Schedules Direct without the lockouts.** Jellyfin's own Schedules Direct provider asks
Schedules Direct for everything on every guide refresh, and Schedules Direct warns that it
gets accounts and IP addresses banned. This plugin downloads only what changed, once a day
(Scheduled Tasks → *Update Schedules Direct guide*, at a time between 2:00 and 6:00 picked
at random for each server, so servers don't all ask at once), and only for the lineups your
sources use. **Update now** asks again at most every 15 minutes. Jellyfin's guide refreshes read the local copy and never reach Schedules
Direct. Programme pictures are fetched the first time a client shows them, then kept, and
downloads stop for the day when Schedules Direct's daily picture limit is reached. A kept
picture older than 14 days (a setting) is re-checked the next time it's shown and downloaded
again only if Schedules Direct has new art.

**XMLTV guides Jellyfin misreads, cleaned.** Some XMLTV sources (Channels DVR's in
particular) need fixing before Jellyfin reads them right. Each fix is a per-source option:

- *Remove a year already in a movie title*: `<title>The Wizard of Oz (1939)</title>` with
  `<date>19390825</date>` makes Jellyfin name the recording "The Wizard of Oz (1939) (1939)".
  The trailing year is removed only when it matches the programme's own date.
- *Give Jellyfin the Gracenote episode ids*: the id in `<episode-num system="tms">`, which
  Jellyfin ignores, is copied to `<episode-num system="dd_progid">`, which it reads. Without
  it Jellyfin falls back to an id hashed from the episode number alone, so S1E5 and S3E5 of
  a show look like the same episode and "skip episodes already in library" cancels one.

**One guide per tuner.** Jellyfin uses only the first guide provider that covers a tuner,
and a provider left on "all tuners" gives every tuner's channels the wrong guide. The plugin
can create and keep each source's Jellyfin guide provider for you, limited to the tuners you
tick, and points out (and offers to remove) any other provider covering the same tuners.

## How it works

- A source is served at `http://<server>:8096/JellyDvrGuides/guide/<address name>.xml`.
  Jellyfin's XMLTV guide provider fetches it like any other XMLTV address.
- **XMLTV source:** fetched at most every *XMLTV: minimum minutes between fetches*
  (default 10), cleaned if asked, and kept on disk.
- **Schedules Direct source:** written from the local copy in the shape Jellyfin reads
  (channel ids are channel numbers, so channels map to the tuner's by number; categories say
  Movie / Series / Sports; the Gracenote id is in `dd_progid`). By default it holds only the
  channel numbers its tuners carry: a lineup lists hundreds of channels your tuner may not,
  and Jellyfin re-reads the whole guide for every channel it refreshes.
- The last good copy of each guide is kept under `<jellyfin data>/jellydvr-guides/`. If a
  source is down, times out or returns an empty guide, that copy is served instead. This
  matters because Jellyfin doesn't check the HTTP status of an XMLTV download, so an error
  page would otherwise become the guide. A copy is remade when its source's settings change.
- The guide and picture addresses need no sign-in, because Jellyfin's downloader sends
  none. By default they only answer this server and the home network. Source addresses come
  only from the plugin settings, so they can't be used to fetch anything else.
- The Schedules Direct password is never stored: the settings page sends it once, and only
  its hash (the form Schedules Direct's API takes) is kept.

## Install

### From the JellyDVR plugin repository (no build needed)

1. Dashboard → Plugins → **Repositories** → add (one list for all the JellyDVR plugins):
   ```
   https://raw.githubusercontent.com/mackandall/jdvr-client/main/plugins/manifest.json
   ```
2. Dashboard → Plugins → **Catalog** → **JellyDVR Guides** → Install, then restart Jellyfin.

### Or build from source

```bash
dotnet build -c Release
sudo ./install.sh   # copies DLL + meta.json + icon.png into the plugin dir, restarts Jellyfin
```

### Tests

```bash
dotnet test tests
```

## Setup

1. For Schedules Direct: Dashboard → Plugins → **JellyDVR Guides** → *Schedules Direct
   account*: sign in, then **Update now** to list your lineups. Add or change lineups on the
   Schedules Direct website.
2. **+ Add guide source**: pick the type (XMLTV address or Schedules Direct lineup), give it
   a name and address name, tick the tuners it's for, and leave *Add it to Jellyfin's guide
   providers for me* ticked. Save.
3. The plugin adds the guide provider and runs Jellyfin's **Refresh Guide**. Jellyfin keeps
   its own copy of each XMLTV download for up to an hour, so a guide that changed source may
   take that long to show.
4. Once a Schedules Direct source is working, remove Jellyfin's own Schedules Direct guide
   provider (Dashboard → Live TV), or it keeps asking Schedules Direct on every refresh.

## Upgrading from XMLTV Guide Cleaner

The settings become sources at start-up: the guide URL becomes the source *Main guide*
(address name `main`), and any "More guides" become sources 2, 3…. The old addresses
(`/XmltvCleaner/guide.xml`, `guide2.xml`…) still answer with the same guides, so existing
Jellyfin guide providers keep working untouched. Recordings made before the year fix keep
their duplicated names; only future recordings are affected.

## License

GPL-2.0-or-later: you can redistribute and change it under the terms of the GNU General Public
License, version 2 or (at your option) any later version. See [LICENSE](LICENSE).

The official source and releases are at <https://github.com/mackandall/jellyfin-plugin-jellydvr-guides>.
