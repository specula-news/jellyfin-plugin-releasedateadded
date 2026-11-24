# Air Date and Metadata Fixer – Jellyfin-plugin

*(tidigare "Release Date Added")*

Gör att **Recently Added** i Jellyfin sorteras efter **sändnings-/releasedatum** i stället för efter när filen skapades,
och fyller i **saknade sändningsdatum och titlar** för avsnitt via seriens **IMDb-/TVDb-ID**.

Kräver **Jellyfin 12.x** (byggd mot 12.0, fungerar på 12.0–12.4).

## Varför gamla serier hamnade först

Jellyfin 12 sorterar *Recently Added* för TV **per serie**, efter det **senaste** datumet bland seriens avsnitt.
Saknar ett enda avsnitt metadata (titeln blir filnamnet, t.ex. `Sister.Wives.S21E06`) behåller det filens datum,
och då hamnar hela serien – även en från 2007 – överst. Version 1.1 löser det i tre steg:

1. **Jellyfins metadata** (TMDb/TVDb/NFO): finns sändningsdatum används det.
2. **Uppslag via IMDb/TVmaze**: saknas datum (eller är titeln ett filnamn) slås avsnittet upp via seriens
   IMDb-/TVDb-ID hos [TVmaze](https://www.tvmaze.com/api) (gratis, ingen nyckel). Med en
   [OMDb-nyckel](https://www.omdbapi.com/apikey.aspx) (gratis) används även IMDb-data och avsnittens IMDb-ID fylls i.
   Sändningsdatum, titel, beskrivning och ID:n sparas på avsnittet. Serier utan ID matchas på namn + år
   och får sina IMDb/TVDb/TVmaze-ID sparade.
3. **Uppskattning** när inget hittas: närmaste avsnitt i samma säsong → säsongens premiär → tidigare/senare säsonger →
   avslutade seriers slutdatum. Filmer/videor utan datum får 1 januari utgivningsåret.

Hittas inget alls behåller objektet Jellyfins datum (räknas som nytt). De listas i en rapport.

## Installation (via plugin-repository)

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. URL:
   ```
   https://raw.githubusercontent.com/specula-news/jellyfin-plugin-releasedateadded/main/manifest.json
   ```
3. **Catalog** → **Air Date and Metadata Fixer** → **Installera** (eller uppdatera), starta om Jellyfin.

Har du redan 1.0 ("Release Date Added") installerad dyker 1.1 upp som uppdatering – samma plugin.

### Manuell installation

Ladda ner zip-filen från [senaste releasen](https://github.com/specula-news/jellyfin-plugin-releasedateadded/releases/latest)
och packa upp den i en egen mapp i Jellyfins `plugins`-mapp. Starta om Jellyfin.

## Efter installation

1. (Valfritt) Lägg in en OMDb-nyckel i pluginets inställningar.
2. Klicka **Kör på hela biblioteket nu** (eller kör *Air Date Fixer: sätt 'Date added' från sändningsdatum*
   under Schemalagda uppgifter). Första körningen tar några minuter på ett stort bibliotek.
3. Läs sammanfattningen i serverloggen (sök `Air Date Fixer`) och rapporten
   `plugins/configurations/Jellyfin.Plugin.ReleaseDateAdded.report.txt`.

Nya avsnitt hanteras automatiskt, och uppgiften körs varje natt kl 04:00.

### Rapporten

Rapporten visar avsnitt som inte gick att få datum för, med orsak, t.ex.:

```
RuPaul's Drag Race UK S16E16 "RuPauls.Drag.Race.S16E16" – inget datum: säsong 16 finns inte hos TVmaze
för "RuPaul's Drag Race UK" (har säsong 1–7) – ligger filen i fel serie eller är numreringen annorlunda?
```

Det betyder oftast att filen ligger i fel serie (här ett avsnitt av amerikanska *RuPaul's Drag Race*) eller att
Sonarr/TVDb numrerar säsongerna annorlunda än TMDb. Flytta/byt namn på filen, eller ändra seriens
*Display order* i Jellyfin, och kör uppgiften igen.

## Inställningar

| Inställning | Standard | Beskrivning |
|---|---|---|
| Aktiverad | På | Huvudbrytare |
| TV-avsnitt / Filmer / Musikvideor / Övriga videor | På | Vilka typer som omfattas (extramaterial rörs aldrig) |
| Slå upp saknade sändningsdatum och titlar | På | TVmaze via IMDb-/TVDb-ID |
| OMDb API-nyckel | – | Valfri, ger IMDb-data och avsnittens IMDb-ID |
| Spara hittad metadata på avsnittet | På | Datum, titel (bara om den ser ut som ett filnamn), beskrivning (bara om den saknas), ID:n. Låsta fält rörs inte |
| Uppskatta datum när inget hittas | På | Se ovan |
| Uppdatera direkt | På | Reagerar när Jellyfin läser in/uppdaterar objekt |
| Behåll klockslaget | På | Releasedag + klockslaget då filen lades till |
| Uppdatera seriens "Date episode added" | På | |
| Hoppa över låsta objekt | På | |
| Skriv även till NFO-filer | Av | |
| Testläge | Av | Loggar bara, ändrar inget |

## Bra att veta

- **Filmer:** TMDb:s premiärdatum är oftast biopremiären, så en film från juni som du laddar ner i oktober får juni.
- Jellyfin grupperar avsnitt som "lagts till" inom 24 timmar till ett säsongs-/seriekort – med sändningsdatum
  gäller det avsnitt som sänts samma dygn.
- Ifylld metadata kan skrivas över av Jellyfin om TMDb senare får bättre data – det är meningen.

## Ångra

Ursprungliga *Date added* sparas i `plugins/configurations/Jellyfin.Plugin.ReleaseDateAdded.originaldates.json`.
Kör **"Air Date Fixer: återställ ursprungliga 'Date added'"** under Schemalagda uppgifter. Datumen läggs tillbaka och
pluginet pausas. Ifylld metadata (titlar, sändningsdatum) ligger kvar.

## Bygga / släppa ny version

Varje push till `main` bygger pluginet med GitHub Actions mot Jellyfins officiella NuGet-paket. En ny release
(zip + `manifest.json`) skapas bara när versionen höjs i `build.yaml`, `meta.json` och `AssemblyVersion` i `.csproj`.

Lokalt (kräver .NET 10 SDK):

```bash
dotnet build Jellyfin.Plugin.ReleaseDateAdded/Jellyfin.Plugin.ReleaseDateAdded.csproj -c Release -o out
```
