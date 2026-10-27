# DexScannerBot — Spike Radar

DexScannerBot scans DexScreener for newly active tokens on Solana, Ethereum and Base. It filters out spam and scams, then estimates **how likely each token is to spike in the next 30 minutes and when**. Every call comes with a wallet-ready plan: the slippage to set in Phantom or Jupiter, the highest price worth paying, a stop loss, profit targets and what the trade costs round trip.

It runs locally on Windows, prints to a console and serves a live dashboard at **http://localhost:3000** (opened automatically).

> **Not financial advice.** No software can know exactly when a coin will move. The engine produces a probability and an estimated window, measures how often its calls come true, and shows that hit rate on the dashboard. Treat early numbers as unproven until the model reports itself as calibrated. Memecoins can go to zero in minutes.

---

## Quick start

**Requirements:** Windows 10/11 with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No API keys are needed for the core features.

**Build a release exe:** double-click `build-release.bat`. It produces a single self-contained `release\DexScannerBot.exe` (plus `release\frontend\index.html`) and writes all output to `build.log`.

Or from a terminal in this folder:

```bat
dotnet publish DexScannerBot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o release
```

**Run:** start `release\DexScannerBot.exe` (or `dotnet run` during development). The dashboard opens in your browser.

**Preview the dashboard without the scanner:** open `frontend\index.html?demo=1` in a browser. It fills the screen with clearly labelled fake data.

If the dashboard fails to start with "Access denied", run once as Administrator or reserve the port:
`netsh http add urlacl url=http://+:3000/ user=Everyone`

---

## What happens to each token

1. **Discover.** Eight scan workers rotate through DexScreener's latest boosts, top boosts, new token profiles and keyword searches, then fetch full pair data in batches of 30. A shared de-duplication list stops the same token being processed by several workers at once.
2. **Gate.** The analyzer rejects stablecoins and majors, spam and phishing names, dead or micro pools, wash-trading signatures, active dumps and tokens that are old and cold. Survivors get a heuristic score; anything below the gate (default 50) is dropped.
3. **Check safety.** Real security reports are fetched: RugCheck for Solana, GoPlus for EVM chains (cached 10 minutes). Confirmed fraud is blocked outright.
4. **Predict.** The spike engine computes a probability, a market phase, a decision with a countdown and an execution plan.
5. **Track live.** Promising tokens go on the live tape and are re-checked every 8 seconds. The prediction updates as the market moves, and a spike alert fires the moment a token flips to *Buy now*.
6. **Learn.** Every call is followed for 30 minutes to see whether it really spiked. Each result trains the model and updates the calibration chart.

---

## The spike engine

### What counts as a spike

A call succeeds if price rises **+25% before it falls 15%, within 30 minutes**. Both are measured from the *effective entry*: the price after estimated price impact, which is what a wallet would actually pay. All three numbers are configurable.

### Signals it reads

| Group | Signals |
|---|---|
| Order flow | Buy share of trades over 5 minutes and 1 hour (discounted when there are too few trades to trust) |
| Bursts | Buys per minute in the last 5 minutes vs the hourly pace; dollar volume pace 5m vs 1h; 1h vs 24h |
| Live tape | Buys per minute in the last 60 seconds vs hourly baseline, live net flow, price slope over 2 minutes, slope curvature (1m vs 4m), breakout above the 5-minute high, liquidity change over 3 minutes |
| Accumulation coil | Strong buying and rising activity while price is still flat — the classic pre-spike signature |
| Late chase | Penalty when the move has already printed (big 1h or 5m gains) |
| Structure | Pool depth, market-cap room, token age, active DexScreener boost, website and socials |
| Risk | Contract and LP security, holder concentration and insider networks, wash/bot volume, dump or liquidity-drain pressure |

### Probability

The signals feed an online logistic model. It starts from hand-set prior weights and adjusts after every resolved outcome, pulled gently back toward the priors so a handful of results can't swing it. The dashboard's "Why this probability" panel shows each signal's contribution with a plain-language note.

### Phases

| Phase | Meaning |
|---|---|
| Accumulation | Buyers stacking, price flat — spike may be close |
| Ignition | Buy burst and price breaking out — the spike is starting |
| Expansion | Trend underway with buyers in control |
| Climax | Vertical move already printed |
| Distribution | Early holders selling into strength |
| Fade / cooling | Momentum gone or going |

### Decisions

| Decision | When | What to do |
|---|---|---|
| **Buy window open** | Ignition or strong expansion, probability ≥ 55%, positive edge after costs | Enter now, within the max entry price |
| **Armed** | Spike building but not confirmed; shows an estimated countdown | Get the swap ready; enter on the breakout or when it flips to Buy |
| **Wait for pullback** | The move already ran | Wait the suggested time for the suggested dip |
| **Avoid** | Distribution, fade, dump pressure, liquidity being pulled or sell tax over 15% | Stay out |
| **No setup** | Nothing actionable | — |

A buy call is automatically downgraded to Armed if round-trip costs exceed the expected edge, or if the pool is so thin that your trade size would move price more than 8%.

**Confidence** (separate from probability) reflects how much evidence backs the number: live tape data, a security report, enough trades, model calibration and agreement between the strongest signals.

---

## Live market tape

DexScreener only gives fixed 5m / 1h / 6h / 24h windows, but spikes start inside a single minute. The tape re-polls every watched token every 8 seconds (up to 120 tokens, batched 30 per request, far below DexScreener's 300 requests/minute limit). It differences successive snapshots to measure what happened in the last 60 seconds. For tokens younger than 24 hours the counts are exact; for older ones it estimates from the rolling 5-minute window.

Tokens stay on the watchlist longer the closer they are to a spike (30 min for Buy, 20 for Armed, 15 for Pullback) and are dropped after three Avoid readings in a row. Every scanned pair is also recorded for free, so tokens build history before they qualify.

---

## Learning and calibration

Every prediction that isn't scam-blocked is followed (up to 600 at a time) with a price check every 20 seconds. It resolves as **spiked** if the target is hit first, or **missed** if it hits the stop, collapses, disappears or times out.

Each result:
- trains the model (so probabilities drift toward reality),
- fills a calibration bucket — the chart shows, for each predicted range (0–10%, …, 90–100%), how often those calls actually spiked,
- updates the Brier score (lower is better; 0.25 is coin-flip guessing),
- updates the hit rate of *Buy now* calls specifically.

The model reports **Learning** until 30 calls have resolved, then **Calibrated**. State is saved to `data\prediction_state.json` inside the folder the app is started from, so learning survives restarts. Delete that file to start fresh.

---

## Wallet execution plan

Shown on every token for a reference size (default $100, set `DEXSB_TRADE_SIZE_USD` to your usual size):

| Field | How it's worked out |
|---|---|
| **Price impact** | Constant-product pool maths: your size ÷ the pool's quote reserve (about half the USD liquidity) |
| **Set slippage to** | Impact + a buffer for how far price is likely to move in the ~20 seconds it takes to confirm in a wallet + any buy tax. Use this instead of *Auto* on thin pools |
| **Don't pay above** | Current price plus a small chase allowance; above it the edge is gone |
| **Keep impact under 2%** | Largest trade size that stays under 2% impact |
| **Stop loss / take profit** | Stop at −15% (wider if the token is very volatile); targets at +25% and +60% from your effective entry |
| **Round trip** | Impact both ways + pool fee + taxes, via Jupiter/Uniswap; and again via Phantom's built-in swap, which adds 0.85% each way |
| **Edge** | probability × target − (1 − probability) × stop − costs |

Buttons open the swap on **Jupiter** (Solana), **Uniswap** (Ethereum, Base, Arbitrum and others) or **PancakeSwap** (BSC); **Open in Phantom** opens that swap inside the Phantom mobile app; **Copy address** copies the contract. Keep a little SOL or ETH for network fees.

---

## Safety layer

**Hard blocks** (never shown as buyable):
- honeypot (sells fail), owner can change balances, ~100% sell tax
- already marked rugged by RugCheck
- **Solana freeze authority still live** — the creator could freeze the tokens inside your wallet so you can't sell
- critical rug-risk score (81+/100) or critical contract risk

**Penalties** lower the probability: mint authority still live, LP less than 50% locked, top-10 holders above 35–50% (pool vaults and the pump.fun bonding curve are excluded from this count), creator holding over 5–10%, insider wallet networks, sell tax over 10%, hidden owner, tax-modifiable contracts, wash-trading patterns, coordinated 5-minute dumps, and liquidity being pulled.

If a security provider can't be reached, the token is treated as *unknown*, not *safe*, and the confidence drops.

---

## Dashboard

- **Orbital radar.** A rotating Earth with tracked tokens as satellites: inner orbit = Buy now (pulsing gold), middle = Armed or Pullback, outer = watching. Hover for the name, click to open.
- **Closest to spiking.** Buy-now and Armed tokens with live countdown bars.
- **Signal feed.** Filter by decision, chain, search and minimum chance; sort by chance, soonest or newest. Each card shows the probability ring, decision and countdown, a 60-second price sparkline, key stats, slippage and edge.
- **Token detail.** Decision and phase track, full execution plan with swap buttons, signal breakdown, live-tape readings, safety report and the DexScreener chart.
- **Engine accuracy.** Calibration status, buy-call hit rate, overall spike rate, Brier score, calibration chart and recent results.
- **Paper trading.** Balance, PnL, win rate, open/closed trades and drawdown.
- **Activity and alerts.** Spike alerts appear as toasts; turn on **Sound** for a chime.

Works on phones too (single column).

---

## Paper trading

Starts with a $1,000 simulated balance and opens a trade on every *Buy now* call (one per token): $400 at ≥75% probability, $200 at ≥60%, otherwise $100, never more than 10% of the balance. Entries and exits pay realistic price impact for that size on that pool plus pool fees and taxes. Exits use the plan's stop and first target, with a trailing stop, and a 4-hour maximum hold. Nothing is ever sent on-chain.

---

## Configuration

Set Windows environment variables before starting (for example `setx DEXSB_TRADE_SIZE_USD 250`, then open a new terminal).

### Spike engine

| Variable | Default | Meaning |
|---|---|---|
| `DEXSB_TRADE_SIZE_USD` | 100 | Reference trade size for impact, slippage and edge |
| `DEXSB_INVEST_THRESHOLD` | 0.55 | Minimum probability for a Buy call |
| `DEXSB_PREDICT_WIN_PCT` | 25 | Spike target, % |
| `DEXSB_SPIKE_STOP_PCT` | 15 | Stop that fails a call, % |
| `DEXSB_SPIKE_HORIZON_MIN` | 30 | Minutes a spike has to happen in |
| `DEXSB_CALIBRATION_MIN` | 30 | Resolved calls before the model reports Calibrated |
| `DEXSB_PHANTOM_FEE_PCT` | 0.85 | Phantom in-wallet swap fee per swap, % |
| `DEXSB_TAPE_POLL_SECONDS` | 8 | Live tape polling interval |
| `DEXSB_MAX_WATCHED` | 120 | Maximum tokens on the live tape |
| `DEXSB_PREDICT_ENABLED` | true | Turn outcome tracking/learning on or off |

### Scanner

| Variable | Default | Meaning |
|---|---|---|
| `DEXSB_GATEKEEP_SCORE` | 50 | Minimum heuristic score to reach the engine |
| `DEXSB_SCANNER_WORKERS` | 8 | Parallel scan workers |
| `DEXSB_SCANNER_BATCH_SIZE` | 30 | Addresses per DexScreener batch |
| `DEXSB_SCANNER_POLL_MS` | 1000 | Pause when a worker has nothing to do |
| `DEXSB_SOLANA_ENABLED` / `DEXSB_ETH_ENABLED` / `DEXSB_BASE_ENABLED` | true | Chains to scan |
| `DEXSB_SECURITY_ENABLED` | true | Fetch RugCheck/GoPlus reports |
| `DEXSB_FRONTEND_PORT` | 3000 | Dashboard port |

### Paper trading and optional services

| Variable | Default | Meaning |
|---|---|---|
| `DEXSB_STOP_LOSS_PCT` / `DEXSB_TRAILING_STOP_PCT` | −0.30 / −0.15 | Fallback stops when no plan exists |
| `DEXSB_MAX_HOLD_AUTO` | 4 | Maximum paper hold, hours |
| `HELIUS_API_KEY` | — | Enables real-time Solana new-pool detection via Helius |
| `DEXSB_DB_HOST`, `DEXSB_DB_PORT`, `DEXSB_DB_NAME`, `DEXSB_DB_USER`, `DEXSB_DB_PASSWORD`, `DEXSB_DB_SSL_MODE` | localhost… | Optional PostgreSQL; falls back to memory when unavailable |

Other variables in `Services/ConfigService.cs` (Redis, Birdeye, Alchemy, monitoring, execution providers) are read but not used by current features.

---

## Interfaces

**WebSocket** `ws://localhost:3000/ws` — messages the dashboard receives:

| Type | Content |
|---|---|
| `token_surfaced` | First sighting of a token with its full prediction and plan |
| `token_update` | Live re-evaluation of a tracked token |
| `spike_alert` | A token just turned Buy now |
| `outcome_resolved` | A call was resolved as spiked or missed |
| `prediction_stats` | Calibration, hit rates, Brier score, recent results, tokens watched |
| `system_status` | Workers, latency, scanned count, uptime |
| `paper_trading_stats`, `paper_trade_opened` | Paper trading |
| `rug_alert`, `new_pool_detected` | Blocked tokens; Helius pool events |

**HTTP:** `/` dashboard, `/health`, and `/api/v1/tokens/search`, `/api/v1/tokens/top`, `/api/v1/tokens/{address}/history`, `/api/v1/trades`, `/api/v1/analytics/summary`, `/api/v1/analytics/performance`. The history endpoints read from PostgreSQL or the in-memory store; nothing currently writes token history into them, so they return empty results.

---

## Project layout

```
Program.cs                  Startup, wiring, broadcasts, paper-trading price loop
Core/
  ScanWorker.cs             Per-worker pipeline: fetch → gate → safety → predict → broadcast
  CoinAnalyzer.cs           Spam/scam gates and heuristic score
  SpikeMonitor.cs           Re-evaluates watched tokens on every live tick, fires alerts
  PaperTradingEngine.cs     Simulated trades with realistic costs
  DexJson.cs                Tolerant DexScreener JSON parsing
  Resilience.cs             Circuit breaker, rate limiter, retries
Services/
  SpikePredictionEngine.cs  Probability, phase, timing, execution plan
  SpikeModel.cs             Online logistic model and prior weights
  MarketTape.cs             Live 8-second polling and short-window features
  OutcomeTracker.cs         Follows calls, labels results, trains, calibrates
  SecurityIntelService.cs   RugCheck (Solana) and GoPlus (EVM) reports
  RugDetectService.cs       Rug-risk scoring from market data + reports
  DexScreenerClient.cs      Discovery feeds and batch fetching
  WebSocketServer.cs        Dashboard, WebSocket and HTTP API
  HeliusWebSocketClient.cs  Optional real-time pool detection
  DatabaseService.cs, HistoryApiService.cs   Optional PostgreSQL storage
Models/                     Data types
frontend/index.html         Dashboard (single file, no build step)
build-release.bat           One-click release build
```

---

## Data sources

All free, no keys: DexScreener (pairs, boosts, profiles, search), RugCheck (Solana security), GoPlus (EVM security). Optional: Helius (with key).

---

## Known limits

- The probability starts from priors, not data. Judge it by the calibration chart once it has a few dozen results.
- DexScreener data lags the chain by a few seconds, and the tape polls every 8 seconds, so the very first seconds of a spike can be missed.
- Countdown times for Armed tokens are estimates from how strongly buying is building, not guarantees.
- Holder and LP data depend on RugCheck/GoPlus being reachable; without them security is "unknown".
- The bot does not place real trades. You execute in your own wallet.

---

## License

Personal use only — see [LICENSE](LICENSE). You can download, run, modify and trade your own money with it, and share it free for others' personal use. Selling it, running it as a paid service, or selling its signals needs written permission from the author.
