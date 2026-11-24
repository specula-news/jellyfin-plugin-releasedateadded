# IMDb Air Date Fixer – Jellyfin plugin

<img src="assets/icon.png" width="96" align="right" alt="IMDb Air Date Fixer icon">

*(formerly "Release Date Added" / "Air Date and Metadata Fixer")*

Orders **Recently Added** in Jellyfin by **air/release date** instead of file creation time, and fills in
**missing episode air dates and titles** using the series' **IMDb/TVDb ID**.

Requires **Jellyfin 12.x** (built against 12.0, works on 12.0–12.4).

## Why old shows ended up first

Jellyfin 12 orders TV in *Recently Added* **per series**, by the **newest** date among the series' episodes.
If a single episode has no metadata (its title becomes the file name, e.g. `Sister.Wives.S21E06`), it keeps
the file's date – and the whole series, even one from 2007, jumps to the top. The plugin works in four steps:

1. **Jellyfin's metadata** (TMDb/TVDb/NFO): if there is an air date, it is used.
2. **Free online sources, matched by IMDb ID**: if the air date is missing (or the title is a file name), the episode is
   looked up via the series' IMDb/TVDb/TMDb ID in these free sources, in this order, until everything is found:

   | Source | Key | What it gives |
   |---|---|---|
   | [TVmaze](https://www.tvmaze.com/api) | none | air date, title, overview |
   | [TMDb](https://www.themoviedb.org/settings/api) | free API key | air date, title, overview, TMDb ID for the series |
   | [Trakt](https://trakt.tv/oauth/applications) | free client ID | air date, title, overview, episode IMDb ID |
   | [Wikidata](https://www.wikidata.org) | none | air date, title, episode IMDb ID (well-known shows) |
   | [OMDb](https://www.omdbapi.com/apikey.aspx) (IMDb data) | free API key | air date, title, episode IMDb ID |

   Air date, title, overview and IDs are saved on the episode. Series without IDs are matched by name + year and get
   their IMDb/TVDb/TMDb/TVmaze IDs saved – which also helps Jellyfin's own providers.
   During the nightly task Jellyfin is also asked to **search again for missing metadata** (with all its own providers)
   for series and movies that still lack dates.
3. **Estimate**: nearest episode in the same season → season premiere → earlier/later seasons →
   end date of ended series. Movies/videos without a date get January 1st of their release year.
4. **Older year for everything else**: items with no date at all get January 1st of their own year or the
   series' start year (fallback 2000), so they **never show up first**. As soon as real metadata appears
   (Jellyfin refresh, or the nightly task finds it), the item gets its real date.

All undated items are listed in a report so you can fix them.

## Installation (plugin repository)

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. URL:
   ```
   https://raw.githubusercontent.com/specula-news/jellyfin-plugin-releasedateadded/main/manifest.json
   ```
3. **Catalog** → **IMDb Air Date Fixer** → **Install** (or update), then restart Jellyfin.

If an earlier version is installed, this one shows up as an update – it is the same plugin.

### Updates

From 1.3, new versions are installed automatically (checked a few minutes after startup and every 6 hours).
Jellyfin then shows **restart required** on the dashboard and "IMDb Air Date Fixer was updated" in the activity log.
The plugin's settings page also shows a banner when an update is available, with **Install update** and
**Restart Jellyfin now** buttons.

### Manual installation

Download the zip from the [latest release](https://github.com/specula-news/jellyfin-plugin-releasedateadded/releases/latest)
and extract it into its own folder inside Jellyfin's `plugins` folder. Restart Jellyfin.

## After installing

1. (Optional) Add an OMDb API key in the plugin settings.
2. Click **Run on the whole library now** (or run *IMDb Air Date Fixer: set 'Date added' from air date*
   under Scheduled Tasks). The first run takes a few minutes on a large library.
3. Check the summary in the server log (search for `IMDb Air Date Fixer`) and the report
   `plugins/configurations/Jellyfin.Plugin.ReleaseDateAdded.report.txt`.

New episodes are handled automatically, and the task runs every night at 04:00.

### The report

The report lists items for which no air date could be found, with the reason and where they were placed, e.g.:

```
RuPaul's Drag Race UK S16E16 "RuPauls.Drag.Race.S16E16" – season 16 does not exist on TVmaze for
"RuPaul's Drag Race UK" (it has season 1–7) – is the file in the wrong series, or numbered differently? → placed at 2019-01-01
```

This usually means the file is in the wrong series (here an episode of the American *RuPaul's Drag Race*) or that
Sonarr/TVDb numbers the seasons differently from TMDb. Move/rename the file or change the series' *Display order*
in Jellyfin, then run the task again.

## Settings

| Setting | Default | Description |
|---|---|---|
| Enabled | On | Master switch |
| TV episodes / Movies / Music videos / Other videos | On | Which item types are covered (extras are never touched) |
| Look up missing air dates and titles | On | TVmaze via IMDb/TVDb ID |
| TMDb API key | – | Optional (free) |
| Trakt client ID | – | Optional (free) |
| Use Wikidata | On | Free, no key |
| OMDb API key | – | Optional (free), adds IMDb data and episode IMDb IDs |
| Ask Jellyfin to search again for missing metadata | On | At most 50 series + 100 movies per run, every 3 days per item |
| Install plugin updates automatically | On | |
| Save found metadata on the episode | On | Air date, title (only if it looks like a file name), overview (only if empty), IDs. Locked fields are never changed |
| Estimate dates when nothing is found | On | See above |
| Give items without any date an older year | On | January 1st of their own/series' year |
| Fallback year | 2000 | Used when there is no year at all |
| Update immediately | On | Reacts when Jellyfin adds/updates items |
| Keep the time of day | On | Release day + the time of day the file was added |
| Update the series' "Date episode added" | On | |
| Skip locked items | On | |
| Also write to NFO files | Off | |
| Dry run | Off | Only logs, changes nothing |

## Good to know

- **Movies:** TMDb's premiere date is usually the theatrical release, so a movie from June downloaded in October gets June.
- Jellyfin groups episodes "added" within 24 hours into a season/series card – with air dates, that means episodes aired on the same day.
- Filled-in metadata may later be overwritten by Jellyfin if TMDb gets better data – that is intended.

## Undo

The original *Date added* values are stored in `plugins/configurations/Jellyfin.Plugin.ReleaseDateAdded.originaldates.json`.
Run **"IMDb Air Date Fixer: restore original 'Date added'"** under Scheduled Tasks. The dates are put back and the
plugin is paused. Filled-in metadata (titles, air dates) is kept.

## Building / releasing a new version

Every push to `main` builds the plugin with GitHub Actions against Jellyfin's official NuGet packages. A new release
(zip + `manifest.json`) is only created when the version is bumped in `build.yaml`, `meta.json` and `AssemblyVersion` in the `.csproj`.

Locally (requires the .NET 10 SDK):

```bash
dotnet build Jellyfin.Plugin.ReleaseDateAdded/Jellyfin.Plugin.ReleaseDateAdded.csproj -c Release -o out
```
