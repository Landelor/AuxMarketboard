# AuxMarketboard

Dalamud plugin scaffold for pricing item lists from Universalis.

## Install

Add the following URL to Dalamud's custom plugin repositories (Settings → Experimental → Custom Plugin Repositories), then install AuxMarketboard from the Plugin Installer:

```
https://raw.githubusercontent.com/Landelor/AuxMarketboard/main/AuxieDalamudRepo/repo.json
```

Alternatively, download the latest release zip directly from the [Releases](https://github.com/Landelor/AuxMarketboard/releases/latest) page.

## Features

- Add items manually with a searchable dropdown and quantity.
- Import list text in either:
  - artisan/teamcraft link import (`Recipes` recipe IDs are resolved to output item IDs when present), or
  - plain text lines (`Item Name x3`, `12345,2`, `Item Name,4`).
- Fetch current marketboard data from Universalis API:
  - `GET /api/v2/{worldDcRegion}/{itemIds}`
  - Uses `listings` and `entries` query parameters to fetch enough recent values.
- HQ/NQ filter toggles (`hq=true`, `hq=false`, or mixed).
- Optional auto-refresh interval.
- Simple rate-limit handling (request pacing + retry on HTTP 429).
- Row editing controls (change qty inline, +/- buttons, remove).
- Show per-item recent unit prices and calculated subtotal.
- Show overall total in gil for the whole list.

## Universalis API notes

- Base URL: `https://universalis.app/api/v2`
- Official docs: [https://docs.universalis.app](https://docs.universalis.app)
- This project currently uses the "Market board current data" endpoint so listings/recent history are available for quantity-based pricing.

## Build

1. Install **.NET 10 SDK** (required by `Dalamud.NET.Sdk/15.0.0`).
2. Install Dalamud SDK resolver/workload:
   - Either clone and use a known-good Dalamud plugin template repo, or
   - set up the local Dalamud SDK feed as documented on [dalamud.dev](https://dalamud.dev/plugin-development/).
3. Ensure NuGet source exists (`nuget.org`) and check setup with:

```powershell
dotnet --list-sdks
dotnet nuget list source
dotnet restore
```

4. Build with:

```powershell
dotnet build
```
