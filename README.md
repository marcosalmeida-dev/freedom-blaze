# Freedom Blaze

Easily convert major world currencies to Bitcoin's smallest unit, Satoshi, with our intuitive app. Stay updated with real-time exchange rates and perform quick conversions to manage your crypto investments effortlessly. Perfect for both beginners and seasoned crypto enthusiasts!
A blaze of freedom in a tyrant world.

## Technologies

* [ASP.NET Core 8](https://docs.microsoft.com/en-us/aspnet/core/introduction-to-aspnet-core)
* [Azure Key Vault](https://learn.microsoft.com/en-us/azure/key-vault/general/overview)
* [Blazor](https://learn.microsoft.com/en-us/aspnet/core/blazor/?view=aspnetcore-8.0&WT.mc_id=dotnet-35129-website)
* [MudBlazor UI](https://mudblazor.com/docs/overview)
* [QRCoder](https://github.com/codebude/QRCoder/)

## Build

Run `dotnet build -tl` to build the solution.

## Run

To run the web application:

```bash
cd .\src\FreedomBlaze\
dotnet watch run
```

Navigate to https://localhost:7029. The application will automatically reload if you change any of the source files.

## Bitcoin news

`BitcoinNews.razor` reads saved daily articles first. When a recent day has no saved news,
the server requests structured articles from OpenAI's Responses API and requires a web
search. The configured model must support both features; see the
[OpenAI web-search guide](https://developers.openai.com/api/docs/guides/tools-web-search).

Configure `OpenAI:ApiKey` on the **server**, using .NET user secrets for local development,
Key Vault, or the `OpenAI__ApiKey` environment variable. `ChatGptApiKey` remains a legacy
fallback. Keep API keys out of client configuration and source control. `OpenAI:Model`
selects the model; changing models does not fix an exhausted API balance.

If the news stops loading, check the server's `OpenAiNewsClient` / `BitcoinNewsService`
logs for the HTTP status and provider error code:

| Error | Action |
| --- | --- |
| `429` with `credit_balance_exhausted`, `insufficient_quota`, or a billing-limit code | Restore API credit or raise the applicable spending limit for the key's account/project. |
| Other `429` errors | Wait for the provider's rate limit to reset. |
| `401` / `403` | Check the server's key and project permissions. |
| Model/tool rejection | Check that the configured model supports Responses, web search, and structured output. |
| Timeout | Check provider latency and `OpenAI:GenerationTimeout` (four minutes by default). The browser news client waits five minutes; keep it longer than the configured generation timeout. |

Failures return HTTP `503` with a reader-safe explanation instead of a successful empty
news list. Billing/configuration failures pause generation for all dates for
`OpenAI:FailureCooldown` (ten minutes by default); saved articles remain readable.
After correcting the account/configuration, wait for that cooldown or use the existing
master-key-protected `POST /api/bitcoin-news/refresh?date=yyyy-MM-dd` endpoint to retry
immediately. The public page's Retry button respects the cooldown.

Run the offline news integration tests with:

```bash
dotnet test tests/FreedomBlaze.Tests/FreedomBlaze.Tests.csproj
```

## Bitcoin transaction tracking

Open `/transactions` to look up a Bitcoin mainnet address or transaction ID. The
tracker shows confirmation status, fees, individual inputs/outputs, and address
balances/history in sats, BTC, and the selected fiat currency. Fiat values use the
current exchange rate. An address lookup covers one address, rather than every
address belonging to a wallet.

Updates run every 30 seconds while the page is open. Loading older address history
pauses automatic updates; Refresh returns to the latest page. Pending transactions
can change or disappear before confirmation. Provider failures retain the last
successful result and its check time.

The server uses the public Esplora-compatible API at `https://mempool.space/api/` by
default. No additional API key or database migration is required. Searches are not
saved to browser storage or placed in URLs. Outbound request logs/traces are disabled
for the tracking client because their URLs contain the searched public identifier.
The configured provider receives those identifiers.

Optional server configuration (the defaults shown are application request budgets,
not a guarantee of the public provider's allowance):

```json
"BitcoinTracking": {
  "BaseUrl": "https://mempool.space/api/",
  "RequestTimeout": "00:00:15",
  "CacheDuration": "00:00:30",
  "FailureCooldown": "00:00:05",
  "MaxCacheEntries": 256,
  "MaxProviderRequestsPerMinute": 60,
  "MaxConcurrentRequests": 4
}
```

Set `BitcoinTracking__BaseUrl` to a mainnet Esplora-compatible endpoint to change
the provider. HTTPS is required. Shared requests, a bounded cache, and provider cooldowns reduce
duplicate requests across Blazor circuits. Saved watchlists, background alerts,
and Lightning payment lookup are separate future features.

Run the tracker validation, client/service, and rendering tests with the existing
`dotnet test tests/FreedomBlaze.Tests/FreedomBlaze.Tests.csproj` command.

## Code Styles & Formatting

The template includes [EditorConfig](https://editorconfig.org/) support to help maintain consistent coding styles for multiple developers working on the same project across various editors and IDEs. The **.editorconfig** file defines the coding styles applicable to this solution.
