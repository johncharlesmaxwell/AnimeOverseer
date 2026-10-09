# AnimeOverseer

AnimeOverseer is a self-hosted anime catalogue and discovery app. It collects metadata from public anime data sources, gives you flexible search, filtering, sorting, and collections, and can connect to your media stack so you can see what you already own or send new titles to Sonarr and Radarr.

> [!IMPORTANT]
> AnimeOverseer is still under active development. Not all planned features are finished, and existing behaviour, configuration, or stored data may change between releases. Expect occasional bugs and review release notes before upgrading an important installation.

## Features

- Browse a locally cached anime catalogue with paging, full-text search, quick filters, and multi-level sorting.
- Build nested filters across titles, formats, seasons, dates, scores, genres, themes, demographics, and library status.
- Create manual watchlists or automatic collections driven by saved filters.
- Sync catalogue data from AniList, MyAnimeList, and AnimeSchedule.
- Schedule catalogue syncs, ranking refreshes, recommendations, and related-anime imports.
- Review uncertain catalogue matches and choose preferred English titles and poster artwork.
- Detect titles already held in Sonarr or Radarr.
- Add series and movies to Sonarr or Radarr from an anime detail page.
- Keep data in a local SQLite database and cache artwork locally.

## Quick start with Docker

Requirements:

- Docker with Docker Compose

The latest published image is available from GitHub Container Registry for `linux/amd64`:

```sh
docker pull ghcr.io/johncharlesmaxwell/animeoverseer:latest
docker run -d \
  --name animeoverseer \
  --restart unless-stopped \
  -p 7272:7272 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:7272 \
  -e "ConnectionStrings__DefaultConnection=Data Source=/app/data/animeoverseer.db" \
  -v animeoverseer_db:/app/data \
  -v animeoverseer_image_cache:/app/wwwroot/images/cache \
  ghcr.io/johncharlesmaxwell/animeoverseer:latest
```

Alternatively, build the image from source with Docker Compose. From the repository root, run:

```sh
docker compose up --build -d
```

Open [http://localhost:7272](http://localhost:7272), then go to **Settings → Data sources** and run a catalogue sync.

Both examples store the database and image cache in named volumes:

- `animeoverseer_db`
- `animeoverseer_image_cache`

To stop the Compose deployment without deleting its data:

```sh
docker compose down
```

> [!CAUTION]
> `docker compose down -v` also deletes the database and image-cache volumes.

## Run locally

Requirements:

- .NET 10 SDK

Restore and start the server:

```sh
dotnet restore AnimeOverseer.sln
dotnet run --project AnimeOverseer.Server
```

Open [http://localhost:5000](http://localhost:5000). The app creates and migrates `AnimeOverseer.Server/animeoverseer.db` on startup and creates its image cache under `AnimeOverseer.Server/wwwroot/images/cache`.

To use another SQLite location, override the connection string:

```powershell
$env:ConnectionStrings__DefaultConnection = "Data Source=C:\data\animeoverseer.db"
dotnet run --project AnimeOverseer.Server
```

## Configure the catalogue

AniList works without credentials. The other optional catalogue sources require:

| Source | Credential |
| --- | --- |
| MyAnimeList | Client ID |
| AnimeSchedule | Application API token |

Enter credentials under **Settings → Data sources**, validate the connection, select the years and seasons you want, and choose **Sync Now**. You can also create independent one-time or recurring schedules for catalogue syncs and AniList enrichment jobs.

The initial import may take a while because providers enforce request limits. Progress and failures are available under **Settings → Job history**.

## Connect media services

Open **Settings → Media servers** to configure any of the following:

| Service | What AnimeOverseer uses it for |
| --- | --- |
| Sonarr | Library matching and series requests |
| Radarr | Library matching and movie requests |
| Plex | Store and validate a server connection |
| Jellyfin | Store and validate a server connection |

For Sonarr and Radarr, you can also select default quality profiles, root folders, monitoring behaviour, and whether a request should immediately start a search.

Settings and integration credentials are stored in the SQLite database. Treat the database and its backups as sensitive data.

## Filter expressions

Filters and automatic collections support calculated operands. Select the formula button beside a field or value, then combine anime fields with nested functions. For example:

```text
length(genres) >= 3
div(mul(episodes, duration), 60) < 10
length(intersection(genres, createArray("Action", "Adventure"))) >= 2
dateDifference(startDate, endDate) > 90
string(rating, "0.00") = "7.10"
string(startDate, "yyyy-MM-dd") = "2026-04-09"
```

Available expression fields include catalogue text, type, status, season, year, episodes, duration, rating, provider IDs, dates, genres, themes, demographics, title aliases, image count, image availability, title-review flags, and library status.

Supported function groups include:

- Text: `toLower`, `toUpper`, `trim`, `concat`, `replace`, `substring`, `slice`, `indexOf`, `lastIndexOf`, `startsWith`, `endsWith`, and `length`.
- Null and conditional: `empty`, `coalesce`, `if`, comparisons, `and`, `or`, and `not`.
- Conversion and math: `string`, `int`, `float`, `decimal`, `bool`, `add`, `sub`, `mul`, `div`, `mod`, `min`, and `max`. `string(value, format)` accepts invariant-culture numeric and date format strings; the format remains optional for existing formulas.
- Date and time: `utcNow`, `addDays`, `addToTime`, `subtractFromTime`, `dateDifference`, day-part functions, start-of-period functions, formatting, and parsing.
- Collections: `array`, `createArray`, `contains`, `intersection`, `union`, `first`, `last`, `join`, `split`, and `sort`.

Common deterministic expressions are translated to SQLite so filtering, counts, and paging remain database-backed. Expressions that cannot be translated are evaluated in memory. Existing relative values such as `currentYear`, `nextSeason`, and `today - 30d` remain supported.

## Development

Build the solution:

```sh
dotnet build AnimeOverseer.sln
```

Run the test suite:

```sh
dotnet test AnimeOverseer.sln
```

The solution contains:

```text
AnimeOverseer.Server/        Blazor Server application, services, and EF Core migrations
AnimeOverseer.Server.Tests/  xUnit unit and SQLite integration tests
Dockerfile                   Multi-stage Linux container build
docker-compose.yml           Local container deployment and persistent volumes
```

Database migrations are applied automatically when the application starts. To add a migration during development, install the EF Core CLI and run:

```sh
dotnet ef migrations add <MigrationName> --project AnimeOverseer.Server
```

## Deployment notes

The container listens on port `7272` and runs as a non-root user. Mount persistent storage at `/app/data` and `/app/wwwroot/images/cache`; the included Compose file already does this.

AnimeOverseer currently has no built-in user authentication. Run it on a trusted network or place it behind an authenticated reverse proxy before exposing it outside your network.

## License

AnimeOverseer is licensed under the [GNU Affero General Public License v3.0](LICENSE).
