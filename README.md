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

`BitcoinNewsBackgroundService` loads today's saved edition on startup and generates it only
when missing. It refreshes the edition daily at **08:00 GMT-3 (11:00 UTC)**,
independent of the server's time zone and daylight saving rules. Startup work begins after
the application has started and does not block HTTP startup. The application must be running
for the daily schedule. Startup after 08:00 catches up only when the edition was saved before
that morning's deadline. A SQL session lease prevents replicas from researching the same day
simultaneously; the saved update time prevents queued replicas/restarts from paying for another
morning refresh. The existing two-replica deployment can keep both workers enabled.

`BitcoinNews.razor` and public `GET /api/bitcoin-news` only read saved editions on normal loads.
The Retry button explicitly requests background generation when today's edition is missing.
`POST /api/bitcoin-news/retry` returns an attempt status immediately; the browser polls
`GET /api/bitcoin-news/retry/{id}` every five seconds and shows search progress. Concurrent
retries share one attempt and respect `OpenAI:FailureCooldown` (ten minutes by default).
Attempt counts and failure state survive restarts in SQL. `OpenAI:MaxDailyGenerationAttempts`
limits automatic jobs to three per edition across replicas; each job makes at most two research
responses. Only the protected operator refresh explicitly bypasses cooldown and automatic-job limits.
Navigating away cancels polling while the worker continues. Saved editions and historical retries
only reread storage. The worker uses OpenAI's Responses
API with required web search and structured output. The configured model must support both; see the
[OpenAI web-search guide](https://developers.openai.com/api/docs/guides/tools-web-search).
Interactive pages also reread today's saved edition every thirty seconds, so startup and morning
results appear without requiring another paid retry. Today's persisted cache expires after at most
one minute so another replica's saved refresh becomes visible; unsaved results retain the long cache TTL.

The default model is **GPT-6.1 Sol** with **medium** reasoning: a practical quality/cost
choice for targeted searches and validation across regions. Its
[model documentation](https://developers.openai.com/api/docs/models/gpt-6.1-sol)
lists support for Responses, web search and structured output. `OpenAI:MaxOutputTokenCount`
defaults to `24000`, covering reasoning as well as the final article JSON.
`OpenAI:MaxWebSearchToolCalls` caps each response at eight built-in tool calls and search uses
medium context. Output limits do not cap retrieved input; web-search calls and search-content
tokens both incur charges ([OpenAI pricing](https://developers.openai.com/api/docs/pricing)).
SDK transport retries are disabled by default to avoid repeating an upstream search after a lost
response. Responses are tagged with application, edition, job and attempt metadata for billing audits.

New editions contain exactly **nine distinct stories**, covering all six inhabited
continents (North America, South America, Europe, Africa, Asia and Oceania), with
at least one story about Bitcoin in **Brazil** and no more than two North American
stories. Brazil counts toward South America. Coverage follows the reported event's
location, not the publisher's headquarters. The card's region label names the event's country.
`OpenAI:NewsArticleCount` must remain `9`.

Prefer same-day articles, but allow earlier reporting within the last **three calendar dates**
ending on the edition date when needed for regional coverage. `OpenAI:NewsLookbackDays`
controls that window (1–7; default 3), including historical refreshes. The original publication
date is preserved and displayed on each card; future or older articles are rejected.

Search is limited to established specialist news portals: CoinDesk, Cointelegraph,
Bitcoin Magazine, Decrypt, Bitcoin.com News, Blockworks, Portal do Bitcoin, Livecoins,
CriptoFácil, BitcoinKE, Crypto News Australia and CoinPost. Brazilian reporting is
searched in Portuguese; other local languages are used where useful. These are
preferred editorial sources, not a measured traffic ranking. Sponsored content,
press releases, duplicate stories and altcoin-only news are excluded by the prompt.

The server validates the article count, continent/country metadata, Brazil coverage,
publication dates and publication links. Tracking parameters and fragments do not
make a duplicate article count as a new story. Discovery returns up to eighteen candidates,
including useful partial findings, plus an explanation of any missing regions. The server
rejects unusable candidates, retains verified findings between searches, reserves a story
for each continent and Brazil, then fills the remaining slots with the freshest eligible
stories. A deficient pool gets **one targeted follow-up search** under the same generation
timeout, focused on its actual gaps. If a complete edition still cannot be assembled, it
is reported unavailable and is not cached or saved as a partial edition. Search diagnostics
record consulted domains, candidate counts, rejected-candidate reasons and the model's
coverage explanation, making scarcity distinguishable from unreadable JSON or API failure.

Startup reuses saved news; the daily 08:00 refresh replaces an edition saved before that deadline.
Saved news stays available when a refresh fails. Use the protected refresh endpoint described
below to replace an edition manually.

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
| Timeout | Check provider latency and `OpenAI:GenerationTimeout` (ten minutes by default, shared by the initial search and its repair). Browser reads do not wait for generation. |

Failures return HTTP `503` with a reader-safe explanation instead of a successful empty
news list. Billing/configuration failures pause generation for all dates for
`OpenAI:FailureCooldown` (ten minutes by default); saved articles remain readable.
After correcting the account/configuration, use the existing master-key-protected
`POST /api/bitcoin-news/refresh?date=yyyy-MM-dd` endpoint to retry immediately, or wait
for the next scheduled refresh. Retry can request a missing current edition again after the
configured failure cooldown, including after account credit has been restored.
Failed scheduled work is logged and does not terminate the worker; its next daily run remains
scheduled. Host shutdown cancels waiting and active generation without recording a provider failure.

Migration `20261012000000_AddNewsGenerationAttempts` adds shared attempt/cooldown state and
is applied by the existing startup migration step. If shared storage or lease acquisition fails,
generation stops before calling OpenAI. A failed article save is reported and the valid cached
edition is marked for persistence-only retry; retrying that save does not research the same news again.
Historical days when the application was stopped are not automatically backfilled.

Repository tests use fake API keys and injected HTTP handlers at `openai.invalid` (or no SDK client).
Neither `dotnet build` nor `dotnet test` launches the real news worker or spends OpenAI credits.

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

Transaction details and history show the actual whole-transaction fee reported by
mempool.space, in sats and the selected currency at current exchange rates. This
fee is not necessarily paid by the tracked wallet and is not subtracted again
from outgoing/net amounts. The top transaction summary shows the Bitcoin price
on its UTC confirmation date in place of sat/vB, using the historical daily
closing quote in the selected currency. Fee estimates use current rates and
remain independent of those historical prices.

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

The Track Bitcoin area also shows current mempool.space network fee recommendations
for No Priority (`economyFee`), Low (`hourFee`), Medium (`halfHourFee`) and High
(`fastestFee`). It uses `/v1/fees/precise`, the same precise recommendations as the
portal, preserving fractional sat/vB rates. The portal rounds its displayed rates;
this app shows up to three decimals and calculates estimates from the full rate.
Estimated costs use a clearly displayed, editable 140 vB transaction size by default,
matching mempool.space's example size: round `rate × vB` up to whole sats, then
convert with the current selected-currency Bitcoin rate. This is a size
estimate, not a measured network average or a transaction's actual paid fee. Labels
are translated into all eight supported app languages. Recommendations load before
any search and update every 30 seconds when auto-refresh is enabled; manual refresh
is independent of wallet history. Failed refreshes retain the last quote with a
stale notice and its timestamp. The existing provider cache and request budgets
also apply to the public fee requests. Older providers without the precise endpoint
fall back to `/v1/fees/recommended` only on HTTP 404; those legacy recommendations
can round Bitcoin rates up to whole sat/vB. An alternate Esplora provider must
support one of these mempool endpoints to supply recommendations. Small fiat
differences from the portal can remain due to whole-satoshi rounding, exchange-rate
sources, and refresh timing.

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
