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

Open `/transactions` to look up a Bitcoin mainnet transaction ID, one public
address, or a watch-only wallet of up to 10 public addresses separated by lines or
commas. The wallet view includes only the supplied addresses; it does not discover
other addresses belonging to the same wallet.

The tracker shows total balance (confirmed plus pending), total received and spent,
confirmation status, fees, summed transaction inputs/outputs, and paginated history.
Lifetime received/spent totals come from complete provider address statistics,
including change and transfers between tracked addresses. History uses the net
change across all tracked addresses, counting each transaction once; outgoing
amounts include fees. Incoming/input values are green and outgoing/output values
are red, with signs and labels. Missing input values remain unknown.

Balance estimates use current exchange rates. Confirmed transaction values use
the BTC-USD daily closing candle for their **UTC confirmation date**, converted
using historical USD-to-selected-currency reference rates for that date. These are
daily estimates, not the exact market price at block time. The UI displays the FX
observation date when the provider uses an earlier available observation, such as
over a weekend. Pending transactions have no confirmation-date price; the current
UTC day's close becomes available after that day ends. Missing prices are shown as
unavailable rather than replaced with today's price or zero.

Daily Bitcoin candles use the keyless
[Coinbase Exchange API](https://docs.cdp.coinbase.com/api-reference/exchange-api/rest-api/products/get-product-candles),
and historical currency conversion uses [Frankfurter](https://frankfurter.dev/).
Dates outside either provider's coverage remain unavailable, including early
Bitcoin dates before Coinbase BTC-USD trading history. Supported currencies match
the existing currency selector. No additional API key is required.

From/to date controls filter loaded confirmed history, inclusively in UTC. Period
totals describe those loaded transactions; load older pages to include more history.
The lifetime wallet totals remain independent of the visible page or date filter.

Received and spent cards also show independent amount-weighted historical average
BTC prices: sum of priced confirmed gross satoshis times their daily BTC price,
divided by the priced satoshis. These match each card's gross accounting, including
change and transfers between tracked addresses. Pending transactions, missing
amounts, incomplete daily candles, and missing prices are excluded. Coverage is
displayed against the complete **confirmed** received/spent totals, so a fully
priced first page is not mistaken for a complete wallet average. Load older history
to extend the averages; date filters affect the table rather than these card
averages. No automatic scan of an unlimited wallet history is performed.

The search starts with one compact row and an aligned Track button. Multiple
addresses can be pasted directly or entered using the expandable address field.
Currency and auto-refresh controls are grouped below the search, and UTC period
filters appear beside the transaction-history heading.

Updates run every 30 seconds while the page is open. Loading older address history
pauses automatic updates; Refresh returns to the latest page. Pending transactions
can change or disappear before confirmation. Provider failures retain the last
successful result and its check time. Wallet history snapshots expire after ten
minutes or a new wallet snapshot; Refresh returns to current history. Opening a
transaction from history preserves the tracked-address context and provides a
Back to wallet action.

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
  "MaxProviderRequestsPerMinute": 120,
  "MaxConcurrentRequests": 4
},
"BitcoinHistoricalPrice": {
  "CandleBaseUrl": "https://api.exchange.coinbase.com/",
  "FxBaseUrl": "https://api.frankfurter.dev/",
  "RequestTimeout": "00:00:30",
  "CacheDuration": "1.00:00:00",
  "FailureCooldown": "00:00:30",
  "MaxCacheEntries": 512,
  "MaxProviderRequestsPerMinute": 120,
  "MaxConcurrentRequests": 2,
  "MaxQueuedRequests": 64
}
```

Set `BitcoinTracking__BaseUrl` to a mainnet Esplora-compatible endpoint to change
the provider. HTTPS is required. Shared requests, a bounded cache, and provider cooldowns reduce
duplicate requests across Blazor circuits. Historical prices load independently of
blockchain results, cache daily source quotes across currencies, and use a bounded
queue with request pacing and provider cooldowns. Saved watchlists, background
alerts, and Lightning payment lookup are separate future features.

Run the tracker validation, client/service, and rendering tests with the existing
`dotnet test tests/FreedomBlaze.Tests/FreedomBlaze.Tests.csproj` command.

## Code Styles & Formatting

The template includes [EditorConfig](https://editorconfig.org/) support to help maintain consistent coding styles for multiple developers working on the same project across various editors and IDEs. The **.editorconfig** file defines the coding styles applicable to this solution.
