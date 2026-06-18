# Stock Analysis

Stock Analysis is a recovered and maintained C# / WPF stock analysis workspace based on the original AIHelper desktop application. The current focus is replacing private 98da market-data dependencies with local cache and public data sources, while preserving the original stock list, search, quote refresh, chart, scan, and export workflows.

## Current Status

- The project source lives under `src/`.
- Recovered runtime dependencies are stored under `recovered-bundle/` and are referenced by `src/AIHelper.csproj`.
- `NetworkHelper` now intercepts the main stock-data endpoints and routes them to local cache or public East Money endpoints where possible.
- `StockViewModel` can load stale local stock-name cache, import a fallback local cache, and incrementally persist online search results.
- Minute and tick export endpoints still need a public-source replacement.

## Repository Layout

```text
.
+-- src/                         # Recovered C# / WPF project
|   +-- AIHelper.Helpers/         # Network, config, command, update helpers
|   +-- AIHelper.Models/          # Stock, holding, chat, and app config models
|   +-- AIHelper.Services/        # Export engine and export config
|   +-- AIHelper.ViewModels/      # Main, stock, chat, login, and log view models
|   +-- AIHelper.Views/           # Recovered WPF views/code-behind
|   +-- AIHelper.csproj
+-- recovered-bundle/             # Recovered dependency DLLs used by the project
+-- AIHelper_REVERSE_SUMMARY.md   # Reverse-engineering notes and interface summary
+-- LICENSE
+-- README.md
```

## Requirements

- Windows
- .NET 8 SDK
- WebView2 Runtime

The current development machine only has .NET Core SDK 3.1 installed, so `dotnet build` fails with `NETSDK1045` until .NET 8 SDK is installed.

## Build

```powershell
dotnet build .\src\AIHelper.csproj
```

## Data Source Notes

The original application depended on `www.98da.com` for stock codes, ETF lists, quotes, and historical K-line data. This repository is being migrated away from that private dependency:

- `/api/quote` is mapped to East Money batch quote data.
- `/api/search` is mapped to East Money suggest/search data.
- `/api/kline-all` and `/api/index` are mapped to East Money daily K-line data.
- `/api/codes` and `/api/etf` prefer `StockNameMap.json` local cache and only attempt public-source sync when no cache exists.

Generated local files such as `StockNameMap.json`, `StockGroups.json`, and export folders are intentionally ignored by Git.

## Development Branches

- `main`: stable initialization branch.
- `develop`: active development branch for ongoing source replacement, cleanup, and feature work.

## License

This repository is licensed under the MIT License. See [LICENSE](LICENSE).
