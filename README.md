# Release Date Added – Jellyfin-plugin

Gör att **Recently Added** i Jellyfin sorteras efter **releasedatum** i stället för efter när filen skapades.

När Jellyfin har hämtat metadata (TMDb/IMDb/TVDb/NFO) för ett nytt avsnitt eller en ny film sätter pluginet
objektets *Date added* (`DateCreated`) till releasedatumet (`PremiereDate`). Det nyaste släppet hamnar då alltid
överst, och gamla avsnitt/filmer du laddar ner i efterhand (eller kvalitetsuppgraderingar) hamnar inte före.

Kräver **Jellyfin 12.x** (byggd mot 12.0, fungerar på 12.0–12.4).

## Installation (via plugin-repository)

1. Jellyfin → **Dashboard → Plugins → Repositories (Kataloger) → +**
2. Namn: `Release Date Added`, URL:
   ```
   https://raw.githubusercontent.com/specula-news/jellyfin-plugin-releasedateadded/main/manifest.json
   ```
3. Gå till **Katalog/Catalog**, välj **Release Date Added** → **Installera**.
4. Starta om Jellyfin. Uppdateringar dyker upp på samma sätt när en ny version släpps.

Varje push till `main` bygger pluginet med GitHub Actions mot Jellyfins officiella NuGet-paket,
lägger zip-filen som en GitHub-release och uppdaterar `manifest.json`. Ny version: höj `version` i
`build.yaml`, `meta.json` och `AssemblyVersion` i `.csproj`, och pusha.

### Manuell installation

Ladda ner zip-filen från [senaste releasen](https://github.com/specula-news/jellyfin-plugin-releasedateadded/releases/latest) och packa upp den i en egen mapp i Jellyfins `plugins`-mapp,
t.ex. `plugins/ReleaseDateAdded_1.0.0.0/`. Starta om Jellyfin.

| Installation | plugins-mapp |
|---|---|
| Docker (jellyfin/jellyfin) | `/config/plugins` |
| Docker (linuxserver/jellyfin) | `/config/data/plugins` |
| Linux (deb/rpm) | `/var/lib/jellyfin/plugins` |
| Windows | `C:\ProgramData\Jellyfin\Server\plugins` |

## Första gången

Nya objekt hanteras automatiskt. För att rätta det som redan finns i biblioteket:

1. (Valfritt) Kryssa i **Testläge** i pluginets inställningar, kör uppgiften och läs serverloggen
   (rader med `[Testläge]`) för att se vad som skulle ändras. Kryssa sedan ur testläget.
2. Klicka **Kör på hela biblioteket nu** i pluginets inställningar, eller kör
   *Dashboard → Schemalagda uppgifter → "Sätt 'Date added' från releasedatum"*.

Uppgiften körs sedan automatiskt varje natt kl 04:00 som säkerhetsnät.

## Logik

| Situation | Resultat |
|---|---|
| Releasedatum finns och har passerat | *Date added* = releasedatum |
| Inget releasedatum i metadatan | Jellyfins eget datum behålls |
| Releasedatum i framtiden (tidszon/tidigt släpp) | Jellyfins datum behålls tills releasedatumet passerat |
| Objektet är låst i metadata-redigeraren | Rörs inte (kan stängas av) |
| Saknade avsnitt (virtuella) | Rörs inte |

Med **Behåll klockslaget** (på som standard) blir datumet *releasedag + klockslaget då filen lades till*.
Avsnitt som släpps samma dag (t.ex. hela säsonger) hamnar då i den ordning de laddades ner.

## Inställningar

| Inställning | Standard | Beskrivning |
|---|---|---|
| Aktiverad | På | Huvudbrytare |
| TV-avsnitt / Filmer / Musikvideor | På | Vilka typer som omfattas |
| Uppdatera direkt | På | Reagerar när Jellyfin läser in/uppdaterar objekt |
| Behåll klockslaget | På | Se ovan |
| Uppdatera seriens "Date episode added" | På | Sorteringen "Date episode added" i TV-biblioteket följer avsnittens releasedatum |
| Hoppa över låsta objekt | På | |
| Skriv även till NFO-filer | Av | Uppdaterar `<dateadded>` i NFO om NFO-sparning är på i biblioteket |
| Testläge | Av | Loggar bara, ändrar inget |

## Bra att veta

- **Filmer:** TMDb:s premiärdatum är oftast biopremiären. En film som gick på bio i juni och som du laddar ner
  i oktober får alltså juni som *Date added*. Gamla filmer du lägger till syns därför inte i Recently Added.
- Om Jellyfin skriver över datumet igen (filbyte, NFO med `<dateadded>`, metadata-uppdatering) rättar pluginet det direkt.
- Pluginet ändrar bara *Date added*. Filer, metadata och visningshistorik påverkas inte.

## Ångra

Pluginet sparar Jellyfins ursprungliga datum i
`plugins/configurations/Jellyfin.Plugin.ReleaseDateAdded.originaldates.json`.
Kör **"Återställ ursprungliga 'Date added' (Release Date Added)"** under Schemalagda uppgifter. Den lägger tillbaka
datumen och pausar pluginet (*Aktiverad* = av). Därefter kan du avinstallera pluginet om du vill.

## Bygga från källkod

Kräver .NET 10 SDK.

```bash
dotnet build Jellyfin.Plugin.ReleaseDateAdded/Jellyfin.Plugin.ReleaseDateAdded.csproj -c Release -o out
```

Bygget hämtar `Jellyfin.Controller`/`Jellyfin.Model` 12.0.0 från NuGet.

## Felsökning

Sök efter `Release Date Added` i serverloggen (Dashboard → Logs). Fungerar inte pluginet efter en
Jellyfin-uppgradering (t.ex. till 13.x): bygg om med nyare `Jellyfin.Controller` och höj `targetAbi` i `meta.json`.
