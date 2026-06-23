# Stock Analysis

Stock Analysis is a recovered and maintained C# / WPF stock analysis workspace based on the original AIHelper desktop application. Stock-market data is provided by East Money, with a local stock-name cache for offline startup and resilience.

## Current Status

- The project source lives under `src/`.
- Recovered runtime dependencies are stored under `recovered-bundle/` and are referenced by `src/AIHelper.csproj`.
- `NetworkHelper` routes every supported stock-data endpoint to East Money and rejects direct requests to other market-data hosts.
- `StockViewModel` can load stale local stock-name cache, import a fallback local cache, and incrementally persist online search results.
- Quotes, code tables, search, daily K-lines, minute bars, tick details, indices, and trading-day checks are all implemented through East Money.

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
+-- LICENSE
+-- README.md
```

## Requirements

- Windows
- .NET 8 SDK
- WebView2 Runtime

The project builds with the .NET 8 SDK pinned by `global.json`.

## Build

```powershell
dotnet build .\src\AIHelper.csproj
```

## Data Source Notes

- `/api/quote`, `/api/search`, `/api/kline-all`, `/api/index`, `/api/minute`, `/api/minute-trade-all`, and `/api/workday` are mapped to East Money.
- `/api/codes` and `/api/etf` use `StockNameMap.json` as a local cache and refresh it from East Money.
- Direct market-data URLs are accepted only when their host is `eastmoney.com` or one of its subdomains.
- Chat is an independent optional service and is disabled unless explicitly configured.

An optional private FastAPI gateway lives in `market-data-server/`. Set
`AkServerUrl` in the app config to `http://127.0.0.1:8000` to try that gateway
first, while retaining the in-client East Money provider as fallback.

Generated local files such as `StockNameMap.json`, `StockGroups.json`, and export folders are intentionally ignored by Git.

## Development Branches

- `main`: stable initialization branch.
- `develop`: active development branch for ongoing source replacement, cleanup, and feature work.

## License

This repository is licensed under the MIT License. See [LICENSE](LICENSE).
