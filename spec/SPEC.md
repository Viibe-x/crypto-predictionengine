# DexScannerBot — Technical Specification & Architectural Blueprint
**Version:** 4.0  
**Date:** 2026-07-19  
**Status:** Draft / Implementation Target

---

## 1. Executive Summary

DexScannerBot currently operates as a **console-based Solana token scanner** built in C# (.NET 8). It consumes DEXScreener REST endpoints, applies a 15+ gate filter with a 0–100 scoring engine, and surfaces high-momentum tokens to the operator.

This specification defines the transformation of DexScannerBot into a **high-frequency DEX sniping bot** with:
- Sub-100ms execution latency from signal to on-chain trade
- Real-time rug-pull detection across contract, liquidity, developer, and social vectors
- Persistent historical database with full-text search and analytics
- A high-fidelity, futuristic web UI with live data feeds
- Multi-chain support (Solana primary, Ethereum, Base) via WebSocket-native architecture

---

## 2. Core Functionality — High-Frequency Sniping

### 2.1 Sniping Lifecycle

```
New Pair Detected → Validate → Score → Execute → Monitor → Exit
     (WebSocket)      (Gate)    (0-100)  (Jupiter/   (TP/SL)  (UniswapX/
                                              0x/Raydium)        CowSwap)
```

### 2.2 Execution Model

| Mode | Description | Target Latency |
|------|-------------|----------------|
| **Auto-Snipe** | Execute immediately on score ≥ threshold | < 50ms |
| **Draft Mode** | Signal only, no execution | < 10ms |
| **Manual Review** | Surface to UI, require confirmation | — |

### 2.3 Order Execution Requirements

- **Solana:** Jupiter Ultra API or Jupiter v6 Swap API. Use `POST /ultra/v1/order` for pre-signed transactions with priority fee estimation.
- **EVM (Ethereum/Base):** UniswapX `POST /v1/quote` or 0x `POST /swap/v1/quote`. Support both EIP-1559 and legacy gas.
- **Slippage:** Dynamic — 0.5% for high-liquidity pairs, up to 3% for micro-caps.
- **Retry Logic:** Exponential backoff, max 3 retries, 200ms base delay. Circuit-breaker on 5 consecutive failures.

### 2.4 Position Management

```
Entry → Partial TP at 2x (25% size) → TP at 5x (50% size) → Trail to breakeven → Exit at 10x or signal flip
Stop Loss: -30% fixed or -15% trailing (whichever hits first)
Max Hold: 4 hours for auto-mode, 24 hours for manual
```

### 2.5 Parallel Scanning

- Maintain N independent scan workers (configurable, default 8).
- Each worker has its own API connection, rate-limit budget, and execution queue.
- Shared scoring cache with atomic read/write to prevent race conditions.

---

## 3. Risk Management & Rug-Pull Detection Engine

### 3.1 Detection Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                    RUG-PULL DETECTION ENGINE                  │
├──────────────┬──────────────┬──────────────┬─────────────────┤
│  Contract    │   Liquidity  │  Developer   │    Social /     │
│  Analysis    │   Analysis   │  Behavior    │    Sentiment    │
├──────────────┼──────────────┼──────────────┼─────────────────┤
│ • Honeypot  │ • Lock check │ • Wallet     │ • Twitter/X    │
│   check      │   ( minting  │   clustering │   sentiment    │
│ • Mintable   │   & burn )   │   (dev wallets│ • Telegram    │
│   authority  │ • LP age     │   vs community│   activity    │
│ • Proxy      │ • LP % owned │ • Token      │ • Website      │
│   pattern    │   by top 10  │   allocation │   legitimacy  │
│ • Malicious  │ • rug-pull   │   (team vs   │ • Bot         │
│   opcodes    │   signature  │   public)    │   detection   │
└──────────────┴──────────────┴──────────────┴─────────────────┘
```

### 3.2 Contract Analysis (Solana)

- **Token Metadata Program:** Verify PDA derivation and mint authority status.
- **Mint Authority:** If not burned/reassigned → flag as `MINTABLE`.
- **Freeze Authority:** If enabled → flag as `FREEZABLE`.
- **Transfer Hook:** Unknown extensions → flag as `UNKNOWN_EXTENSION`.
- **Supply Analysis:** Compare `max_supply` vs circulating. Large undisclosed supply = flag.
- **Bytecode Pattern Matching:** Regex against known honeypot bytecode sequences (e.g., `11111111111111111111111111111111` as signer constraints).

### 3.3 Liquidity Analysis

- **LP Lock Check:** Verify LP tokens are locked in a known program (e.g., `Lockr`, `Streamflow`). Parse lock expiry timestamp.
- **LP Ownership Concentration:** If top 10 wallets hold > 40% of LP → `HIGH_CONCENTRATION`.
- **Rug-Pull Signature:** Simultaneous LP removal + sell pressure within 1 block → immediate `RUG_DETECTED`.
- **Burn Verification:** Confirm burn transaction on-chain (not just metadata claim).

### 3.4 Developer Wallet Behavior

- **Wallet Clustering:** Use heuristics (shared funding source, timing correlation) to group wallets controlled by same entity.
- **Dev Sell Detection:** Monitor dev wallets for large sells before community pumps.
- **Token Allocation:** If dev wallets hold > 15% of circulating supply → `DEV_HEAVY`.
- **Funding Source:** Trace dev wallet funding back to CEX or mixer. Unknown/clean funding → lower risk. Tornado Cash / unknown mixer → immediate flag.

### 3.5 Social & Sentiment

- **Twitter/X:** Scrape recent tweets for the token handle. Sentiment score via keyword weighting. Bot-like posting patterns (exact duplicates, high frequency) → flag.
- **Telegram:** Monitor group activity. Low message count + high buy pressure = pump group manipulation.
- **Website:** Check domain age (WHOIS), hosting provider, SSL validity. No website = `NO_PRESENCE` flag (not automatic disqualifier but warning).
- **Bot Detection:** Temporal analysis of social posts. Posting intervals < 30s from multiple accounts with identical text → `COORDINATED_CAMPAIGN`.

### 3.6 Risk Score Integration

Combine all vectors into a unified `RugRiskScore` (0–100):

```
RugRiskScore = 
    ContractRisk * 0.35 +
    LiquidityRisk * 0.30 +
    DeveloperRisk * 0.20 +
    SocialRisk * 0.15

Risk Tiers:
  0-20:  SAFE        → Proceed
  21-40: LOW_RISK    → Proceed with standard size
  41-60: MEDIUM_RISK → Proceed with reduced size (50%)
  61-80: HIGH_RISK   → Skip or require manual approval
  81-100: CRITICAL   → Block execution, alert user
```

---

## 4. Data Management — Historical Database & Search

### 4.1 Database Schema (PostgreSQL)

```sql
-- Tokens (canonical, deduplicated by address+chain)
CREATE TABLE tokens (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    address         TEXT NOT NULL,
    chain           TEXT NOT NULL,
    symbol          TEXT NOT NULL,
    name            TEXT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(address, chain)
);

-- Pair snapshots (time-series, append-only)
CREATE TABLE pair_snapshots (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    token_id        UUID REFERENCES tokens(id),
    dex_id          TEXT NOT NULL,
    pair_address    TEXT,
    price_usd       TEXT,
    market_cap      DOUBLE PRECISION,
    fdv             DOUBLE PRECISION,
    liquidity_usd   DOUBLE PRECISION,
    vol_5m          DOUBLE PRECISION,
    vol_1h          DOUBLE PRECISION,
    vol_6h          DOUBLE PRECISION,
    vol_24h         DOUBLE PRECISION,
    price_change_5m DOUBLE PRECISION,
    price_change_1h DOUBLE PRECISION,
    price_change_6h DOUBLE PRECISION,
    price_change_24h DOUBLE PRECISION,
    buys_5m         INT, sells_5m INT,
    buys_1h         INT, sells_1h INT,
    buys_24h        INT, sells_24h INT,
    snapshot_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Analysis results (one per scan event)
CREATE TABLE analyses (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    token_id        UUID REFERENCES tokens(id),
    snapshot_id     UUID REFERENCES pair_snapshots(id),
    total_score     DOUBLE PRECISION NOT NULL,
    profit_tier     TEXT NOT NULL,
    verdict         TEXT NOT NULL,
    risk_level      TEXT NOT NULL,
    rug_risk_score  INT NOT NULL DEFAULT 0,
    entry_signal    TEXT,
    estimated_multiplier DOUBLE PRECISION,
    profit_confidence    DOUBLE PRECISION,
    bullish_signals      JSONB,
    warning_signals      JSONB,
    profit_reasons       JSONB,
    analyzed_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Trades (executed orders)
CREATE TABLE trades (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    token_id        UUID REFERENCES tokens(id),
    analysis_id     UUID REFERENCES analyses(id),
    side            TEXT NOT NULL CHECK (side IN ('BUY', 'SELL')),
    size_sol        DOUBLE PRECISION,
    size_usd        DOUBLE PRECISION,
    entry_price     TEXT,
    exit_price      TEXT,
    realized_pnl    DOUBLE PRECISION,
    realized_pnl_pct DOUBLE PRECISION,
    tx_hash         TEXT,
    status          TEXT NOT NULL CHECK (status IN ('PENDING', 'CONFIRMED', 'FAILED', 'CANCELLED')),
    executed_at     TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Scan sessions
CREATE TABLE scan_sessions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    started_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at        TIMESTAMPTZ,
    scanned_count   INT NOT NULL DEFAULT 0,
    surfaced_count  INT NOT NULL DEFAULT 0,
    rejected_count  INT NOT NULL DEFAULT 0,
    source          TEXT
);
```

### 4.2 Search API

```sql
-- Full-text search on token names/symbols
CREATE INDEX idx_tokens_search ON tokens USING gin(to_tsvector('english', symbol || ' ' || name));

-- Time-range queries on pair_snapshots
CREATE INDEX idx_pair_snapshots_token_time ON pair_snapshots(token_id, snapshot_at DESC);

-- Analysis lookups
CREATE INDEX idx_analyses_token_time ON analyses(token_id, analyzed_at DESC);
```

### 4.3 Query Examples

```sql
-- Top scoring tokens in last 24h
SELECT t.symbol, t.name, a.total_score, a.profit_tier, a.analyzed_at
FROM analyses a
JOIN tokens t ON a.token_id = t.id
WHERE a.analyzed_at > now() - interval '24 hours'
ORDER BY a.total_score DESC
LIMIT 50;

-- Trade performance by day
SELECT date_trunc('day', executed_at) as day,
       COUNT(*) as trades,
       SUM(realized_pnl) as total_pnl,
       AVG(realized_pnl_pct) as avg_pnl_pct
FROM trades
WHERE status = 'CONFIRMED'
GROUP BY 1
ORDER BY 1 DESC;
```

---

## 5. User Interface — High-Fidelity Futuristic Design

### 5.1 UI Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                        DEXSCANNER BOT v4                         │
├────────────┬────────────┬──────────────────┬─────────────────────┤
│            │            │                  │                     │
│  SCAN      │  MARKET    │  LIVE FEED       │  ANALYSIS PANEL     │
│  PIPELINE  │  OVERVIEW  │  (WebSocket)     │  (Selected Token)   │
│            │            │                  │                     │
│  Workers   │  Top movers│  • New pairs     │  • Score gauge      │
│  Status    │  24h vol   │  • Rug alerts    │  • Profit intel     │
│  Queue     │  Trends    │  • Trade exec    │  • Risk matrix      │
│  Throughput│  Heatmap   │  • System log    │  • Contract audit   │
│            │            │                  │                     │
├────────────┴────────────┴──────────────────┴─────────────────────┤
│  HISTORY / SEARCH BAR  │  Trade Log  │  Performance Stats     │
└──────────────────────────────────────────────────────────────────┘
```

### 5.2 Design Language

- **Theme:** Dark mode primary, #0a0a0f background, #00ff88 accent (matrix green), #ff3366 danger (rug alerts).
- **Typography:** JetBrains Mono / Inter for data; Fira Code for addresses.
- **Animations:** CSS transitions on score changes (200ms ease), pulse on new surfacing, slide-in for alerts.
- **Data Viz:** Canvas-based mini sparklines for price/volume; SVG radial gauges for scores.

### 5.3 Technology Stack

| Layer | Technology | Rationale |
|-------|-----------|-----------|
| **Frontend** | React 18 + TypeScript + Vite | Fast HMR, component ecosystem |
| **State** | Zustand or Redux Toolkit | Lightweight, devtools integration |
| **Charts** | TradingView Lightweight Charts or D3.js | Professional financial viz |
| **Real-time** | Socket.io or raw WebSocket | Sub-50ms UI updates |
| **Styling** | Tailwind CSS v3 + Framer Motion | Rapid UI development, animations |
| **Desktop Wrapper** | Tauri v2 (Rust) or Electron | If desktop app needed; otherwise pure web |

### 5.4 Key UI Components

- **Scan Pipeline Monitor:** Real-time worker status, queue depth, API latency.
- **Live Feed:** Auto-scrolling list of new pairs with instant color-coded risk scores.
- **Rug Alert Modal:** Full-screen overlay with red pulsing border when CRITICAL risk detected.
- **Trade Terminal:** One-click execute with slippage preview, gas estimate, and confirmation.
- **History Explorer:** Calendar picker + full-text search + filters (chain, score, profit tier).
- **Performance Dashboard:** Win rate, avg PnL, Sharpe ratio, max drawdown.

---

## 6. Technical Stack & API Integrations

### 6.1 Recommended Stack

| Component | Recommendation | Alternatives |
|-----------|---------------|-------------|
| **Core Engine** | **Go (Golang)** or **Rust** | C# (existing), Node.js |
| **Database** | **PostgreSQL 16+** (TimescaleDB optional) | ClickHouse, SQLite (dev) |
| **Cache** | **Redis** (pub/sub, rate-limit buckets) | Memcached |
| **Frontend** | **React + TypeScript** | SvelteKit, Next.js |
| **WebSocket** | **gorilla/websocket** (Go) or **tokio-tungstenite** (Rust) | Socket.io |
| **Message Queue** | **NATS JetStream** or Redis Streams | Kafka (overkill) |
| **Observability** | **Prometheus + Grafana** | Datadog, OpenTelemetry |
| **Secrets** | **HashiCorp Vault** or environment + age encryption | AWS Secrets Manager |

### 6.2 Why Go or Rust for Core

| Factor | Go | Rust | Existing C# |
|--------|----|------|-------------|
| **Latency** | Sub-millisecond GC pauses | Zero-cost abstractions, no GC | ~1ms GC pauses |
| **Concurrency** | Goroutines + channels (excellent) | Async/await + tokio | TPL (adequate) |
| **WebSocket** | Mature, performant | Mature, performant | System.IO.Pipelines |
| **Deployment** | Single static binary | Single static binary | .NET runtime required |
| **Learning Curve** | Low | High | Existing codebase |

**Recommendation:** Rewrite core engine in **Go** for fastest time-to-production with best latency characteristics. Keep existing C# UI as fallback or migrate to web UI.

### 6.3 API Integrations

#### 6.3.1 Data Feeds (WebSocket + REST)

| Data Source | Protocol | Usage |
|-------------|----------|-------|
| **DEXScreener** | REST (polling fallback) | Token discovery, pair metadata |
| **Birdeye** | WebSocket + REST | Solana real-time price, volume |
| **Jupiter** | REST | Swap quotes, route optimization |
| **Helius** | WebSocket | Solana account subscriptions (new pools, LP events) |
| **Alchemy/Infura** | WebSocket | Ethereum/Base mempool, pending txs |
| **0x API** | REST | EVM swap quotes |
| **Twitter/X API** | Webhook + REST | Social sentiment scraping |
| **Telegram** | MTProto | Channel monitoring |

#### 6.3.2 WebSocket Subscriptions (Priority Order)

1. **Helius Geyser gRPC / WebSocket** — New pool creations on Solana (< 500ms detection)
2. **Birdeye WebSocket** — Real-time price ticks for watched pairs
3. **Ethereum Mempool (Alchemy)** — Pending txs for sniping launches on Uniswap
4. **Base Mempool (Alchemy)** — Same as above for Base chain

### 6.4 System Architecture Diagram

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  WebSocket   │────▶│  Event       │────▶│  Scoring     │
│  Ingestors   │     │  Bus (NATS)  │     │  Engine      │
│  (Helius,    │     │              │     │  (Go/Rust)   │
│  Birdeye,    │     │              │     │              │
│  Alchemy)    │     │              │     │              │
└──────────────┘     └──────┬───────┘     └──────┬───────┘
                            │                    │
                            ▼                    ▼
                    ┌──────────────┐     ┌──────────────┐
                    │  Rug-Pull    │     │  Trade       │
                    │  Detector    │────▶│  Executor    │
                    │  (Async)     │     │  (Jupiter,   │
                    │              │     │   0x, etc.)  │
                    └──────────────┘     └──────┬───────┘
                                             │
                            ┌────────────────┴────────────────┐
                            ▼                                 ▼
                    ┌──────────────┐                 ┌──────────────┐
                    │  Position    │                 │  Database    │
                    │  Manager     │────────────────▶│  (Postgres)  │
                    │  (TP/SL)     │                 │              │
                    └──────────────┘                 └──────────────┘
                                                             │
                            ┌────────────────────────────────┴────────────────┐
                            ▼                                                 ▼
                    ┌──────────────┐                                 ┌──────────────┐
                    │  WebSocket   │─────────────────────────────────▶│  Frontend    │
                    │  Server      │   (Live feed, UI updates)         │  (React)     │
                    └──────────────┘                                 └──────────────┘
```

### 6.5 Rate Limiting & Resilience

```go
// Token bucket per API endpoint
type RateLimiter struct {
    mu       sync.Mutex
    tokens   int
    max      int
    rate     time.Duration
    lastTime time.Time
}

// Circuit breaker per data source
type CircuitBreaker struct {
    failures    int
    threshold   int
    state       State // Closed, Open, HalfOpen
    retryAfter  time.Time
}
```

### 6.6 Latency Budget

| Stage | Target | Critical |
|-------|--------|----------|
| WebSocket → Event Bus | < 5ms | < 10ms |
| Scoring Engine | < 10ms | < 20ms |
| Rug Detection | < 20ms (async) | < 50ms |
| Trade Execution | < 30ms | < 50ms |
| DB Write | < 5ms | < 10ms |
| UI Update | < 50ms | < 100ms |
| **End-to-End** | **< 100ms** | **< 200ms** |

---

## 7. Security & Compliance

### 7.1 Key Management

- **Hot Wallet:** Separate wallet with limited balance (max $500 equivalent). Daily withdrawal limit to cold storage.
- **Private Keys:** Encrypted at rest using AES-256-GCM. Decrypted only in memory during execution.
- **Key Rotation:** Automated weekly rotation with zero-downtime migration.

### 7.2 API Security

- All outbound connections over TLS 1.3.
- mTLS where supported (Helius, Alchemy enterprise).
- Request signing for exchange APIs.

### 7.3 Input Validation

- All external data (pair data, prices) validated against schema before scoring.
- Null checks, type assertions, range checks on all numeric values.
- Reject malformed/overflow values rather than crashing.

---

## 8. Deployment & Operations

### 8.1 Infrastructure

```
┌─────────────────────────────────────────────────────────────┐
│                    Cloud / Bare Metal                        │
│                                                             │
│  ┌────────────┐  ┌────────────┐  ┌─────────────────────┐  │
│  │ Scanner     │  │ API        │  │ Frontend             │  │
│  │ Workers     │  │ Gateway    │  │ (React + Nginx)      │  │
│  │ (x8)        │  │ (Go)       │  │                      │  │
│  └────────────┘  └────────────┘  └─────────────────────┘  │
│                                                             │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ PostgreSQL + Redis + NATS JetStream                  │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                             │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ Monitoring: Prometheus + Grafana + Alertmanager       │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### 8.2 Monitoring & Alerts

| Metric | Alert Threshold |
|--------|----------------|
| Scanner worker queue depth | > 1000 for 30s |
| API error rate | > 5% over 1min |
| Trade execution latency | p99 > 200ms for 5min |
| Wallet balance | < $50 |
| Rug detection hits | Any CRITICAL |
| System CPU | > 80% for 2min |

---

## 9. Migration Path from Current Codebase

### Phase 1: Database Foundation (Week 1-2)
- Set up PostgreSQL, define schema, create EF Core / GORM / sqlx models.
- Migrate existing `history.txt` to `analyses` and `tokens` tables.
- Build basic CRUD API endpoints.

### Phase 2: Core Engine Rewrite (Week 3-6)
- Port `AnalysisEngine` scoring logic to Go/Rust.
- Add WebSocket ingestors for Helius + Birdeye.
- Integrate PostgreSQL persistence.
- Add rug-pull detection module (contract, liquidity, dev wallets).

### Phase 3: Trade Execution (Week 7-9)
- Implement Jupiter Ultra client for Solana.
- Implement 0x/UniswapX client for EVM chains.
- Build position manager with TP/SL logic.
- Paper trading mode for validation.

### Phase 4: Real-Time Frontend (Week 10-13)
- Build React dashboard with WebSocket backend.
- Implement live feed, analysis panel, trade terminal.
- Add history explorer with search.

### Phase 5: Hardening & Launch (Week 14-16)
- Load testing, chaos engineering.
- Security audit, key rotation.
- Gradual rollout: paper → small live → full live.

---

## 10. Open Questions / Decisions Needed

1. **Chain Priority:** Solana-only for v4, or simultaneous EVM launch?
2. **Execution Mode:** Auto-trade with manual override, or signal-only with optional execution?
3. **Funding:** Self-funded dev wallet size, profit split model?
4. **Hosting:** Self-hosted, VPS (e.g., Hetzner), or cloud (AWS/GCP)?
5. **Legal:** Jurisdiction, terms of service, user acknowledgment of risk?

---

## Appendix A: File Structure (Target)

```
DexScannerBot/
├── cmd/
│   ├── scanner/          # Scanner worker binary
│   ├── api/              # REST/WebSocket gateway
│   └── bot/              # Main orchestrator
├── internal/
│   ├── engine/
│   │   ├── scorer.go     # Port of AnalysisEngine
│   │   ├── rugdetect/
│   │   │   ├── contract.go
│   │   │   ├── liquidity.go
│   │   │   ├── developer.go
│   │   │   └── social.go
│   │   └── risk.go
│   ├── ingest/
│   │   ├── helius.go
│   │   ├── birdeye.go
│   │   └── alchemy.go
│   ├── exec/
│   │   ├── jupiter.go
│   │   ├── zero_x.go
│   │   └── position.go
│   ├── db/
│   │   ├── postgres.go
│   │   └── queries.go
│   └── ws/
│       ├── hub.go
│       └── clients.go
├── frontend/
│   ├── src/
│   │   ├── components/
│   │   ├── pages/
│   │   └── hooks/
│   └── package.json
├── deploy/
│   ├── docker-compose.yml
│   ├── Dockerfile
│   └── prometheus.yml
├── spec/
│   └── SPEC.md           # This file
├── go.mod
└── README.md
```

---

## Appendix B: Dependencies (Go)

```go
module github.com/viibe/dexscannerbot

go 1.22

require (
    github.com/gorilla/websocket v1.5.1
    github.com/lib/pq v1.10.9
    github.com/redis/go-redis/v9 v9.5.1
    github.com/nats-io/nats.go v1.36.0
    github.com/shopspring/decimal v1.3.1
    github.com/jackc/pgx/v5 v5.5.3
    github.com/prometheus/client_golang v1.18.0
    github.com/spf13/viper v1.18.2
    solana-go v0.0.0  # github.com/gagliardetto/solana-go
)
```

---

## Appendix C: Scoring Engine — Porting Reference

### C.1 C# → Go Gate Mapping

| C# Gate | C# Location | Go Port | Notes |
|---------|------------|---------|-------|
| Chain filter | `Analyze()` L37-38 | `scorer.go:ValidateChain()` | Hardcoded `solana` in v4 |
| Blacklist | `HashSet<string> Blacklist` | `scorer.go:blacklist.go` | Same set |
| Name red flags | `NameRedFlags[]` | `scorer.go:nameFlags.go` | Same array |
| Liquidity gate | `if (liq < 3_000)` | `scorer.go:LiquidityGate()` | Keep threshold |
| Volume gate | `if (vol24 < 5_000)` | `scorer.go:VolumeGate()` | Keep threshold |
| Market cap gate | `if (cap > 5_000_000)` | `scorer.go:MarketCapGate()` | Keep threshold |
| FDV/Liq gate | `if (fdvLiq > 80)` | `scorer.go:FdvLiqGate()` | Keep threshold |
| Buy pressure gate | `if (buyPct1h < 45)` | `scorer.go:BuyPressureGate()` | Keep threshold |
| Vol/Liq bounds | `if (vlr < 1 || vlr > 2000)` | `scorer.go:VolLiqGate()` | Keep bounds |
| Age gate | `if (ageHours > 168 && volAccel < 2)` | `scorer.go:AgeGate()` | Keep threshold |
| Vol acceleration | `if (volAccel < 0.3)` | `scorer.go:VolAccelGate()` | Keep threshold |
| Score threshold | `if (score < GATEKEEP)` | `scorer.go:ScoreGate()` | GATEKEEP = 60 |

### C.2 Scoring Categories (0-100 points)

| Category | Max Pts | C# Lines | Go Implementation |
|----------|---------|----------|-------------------|
| Volume Acceleration | 30 | L159-164 | `score_volume.go` |
| 5m Hyper Acceleration | 20 | L167-170 | `score_volume.go` |
| Buy Pressure | 25 | L173-177 | `score_pressure.go` |
| Momentum Stack | 15 | L180-193 | `score_momentum.go` |
| Transaction Velocity | 10 | L196-200 | `score_velocity.go` |
| Age Modifier | ±10 | L203-206 | `score_age.go` |
| Market Cap Scale | +15 to -5 | L209-214 | `score_cap.go` |
| Liquidity Health | +0 to -8 | L217-220 | `score_liquidity.go` |
| Boost Bonus | +5 | L223-227 | `score_boost.go` |
| Social Bonus | +5 | L230-231 | `score_social.go` |

### C.3 Profit Intelligence Port

| C# Field | C# Lines | Go Struct Field | Calculation |
|----------|----------|----------------|-------------|
| `EstimatedMultiplier` | L241-251 | `Profit.EstimatedX` | `baseX * scoreMult * accelMult` |
| `ConservativeTarget` | L250 | `Profit.ConservativeX` | `estX * 0.4` |
| `AggressiveTarget` | L251 | `Profit.AggressiveX` | `estX * 2.5` |
| `EntrySignal` | L258-260 | `Profit.EntrySignal` | Ternary on volAccel/buyPct/momentum |
| `ProfitConfidence` | L263-264 | `Profit.Confidence` | `score*0.6 + bool bonuses` |
| `SlippageRisk` | L254-256 | `Liquidity.SlippageTier` | LOW/MED/HIGH by liq threshold |

### C.4 Data Model Mapping

```csharp
// C# DexPair → Go Pair
public class DexPair { ... }
```

```go
// Go equivalent
type Pair struct {
    ChainID     string
    DexID       string
    BaseToken   *Token
    PriceUsd    string
    MarketCap   float64
    Fdv         float64
    Liquidity   *Liquidity
    Volume      *VolumeWindow
    PriceChange *ChangeWindow
    Txns        *TxnWindow
    Info        *PairInfo
    Boosts      *BoostInfo
    Url         string
    PairCreatedAt int64
}

type Token struct {
    Address string
    Symbol  string
    Name    string
}
```

### C.5 Concurrency Considerations

The C# `AnalysisEngine.Analyze()` is synchronous. In Go:

```go
func (e *Scorer) Score(ctx context.Context, p *Pair) (*Analysis, error) {
    // Gates are pure functions — no shared state, safe for concurrent calls
    if err := ValidateChain(p); err != nil {
        return nil, err
    }
    if err := BlacklistCheck(p); err != nil {
        return nil, err
    }
    // ... all gates ...
    
    // Scoring uses local scratch variables
    score := 0.0
    bullish := make([]string, 0, 10)
    // ...
    
    return &Analysis{
        Score:       score,
        Bullish:     bullish,
        // ...
    }, nil
}
```

No mutex needed during scoring. Shared state only in result publication.

---

## Appendix D: WebSocket Message Protocol

### D.1 Server → Client (Frontend)

All messages are JSON objects with a `type` discriminator.

```json
{
  "type": "token_surfaced",
  "payload": {
    "id": "uuid",
    "symbol": "BONK",
    "name": "Bonk",
    "chain": "solana",
    "dex": "raydium",
    "totalScore": 87,
    "profitTier": "NUKE LAUNCH 🚀🚀🚀",
    "riskLevel": "🟢 LOW",
    "verdict": "🚀🚀🚀 NUKE LAUNCH — PARABOLIC SETUP DETECTED",
    "priceUsd": "0.00001234",
    "marketCap": 450000,
    "liquidityUsd": 28000,
    "vol24h": 95000,
    "buyPressure1h": 78.5,
    "volumeAcceleration": 12.3,
    "estimatedMultiplier": 28.5,
    "entrySignal": "⚡ ENTER NOW",
    "bullishSignals": ["🔥 PARABOLIC vol acceleration"],
    "warningSignals": [],
    "address": "7xKX...",
    "chartUrl": "https://dexscreener.com/solana/...",
    "timestamp": "2026-07-19T14:02:00Z"
  }
}
```

```json
{
  "type": "rug_alert",
  "payload": {
    "id": "uuid",
    "tokenId": "uuid",
    "symbol": "SCAM",
    "riskScore": 92,
    "riskTier": "CRITICAL",
    "vectors": {
      "contract": { "honeypot": true, "mintable": true },
      "liquidity": { "rugSignature": true, "lpConcentration": 0.85 },
      "developer": { "devHeavy": true, "mixedFunding": true },
      "social": { "botCampaign": true }
    },
    "recommendedAction": "BLOCK",
    "timestamp": "2026-07-19T14:02:00Z"
  }
}
```

```json
{
  "type": "trade_executed",
  "payload": {
    "tradeId": "uuid",
    "tokenId": "uuid",
    "side": "BUY",
    "sizeUsd": 100.0,
    "sizeSol": 0.45,
    "entryPrice": "0.00001234",
    "txHash": "3a7b...",
    "status": "CONFIRMED",
    "executedAt": "2026-07-19T14:02:05Z"
  }
}
```

```json
{
  "type": "position_update",
  "payload": {
    "tradeId": "uuid",
    "currentPrice": "0.00001567",
    "unrealizedPnlPct": 27.1,
    "unrealizedPnlUsd": 27.10,
    "tpLevel": 1,
    "slDistancePct": -15.0,
    "timestamp": "2026-07-19T14:02:10Z"
  }
}
```

```json
{
  "type": "system_status",
  "payload": {
    "workersActive": 8,
    "queueDepth": 142,
    "apiLatencyMs": 23,
    "tradesToday": 5,
    "pnlToday": 312.45,
    "uptimeSeconds": 86400,
    "timestamp": "2026-07-19T14:02:00Z"
  }
}
```

### D.2 Client → Server (Frontend Actions)

```json
{
  "type": "subscribe",
  "channels": ["tokens", "rug_alerts", "trades", "positions", "system"]
}
```

```json
{
  "type": "manual_trade",
  "payload": {
    "tokenAddress": "7xKX...",
    "chain": "solana",
    "sizeUsd": 100.0,
    "slippageBps": 150
  }
}
```

```json
{
  "type": "toggle_mode",
  "payload": { "mode": "auto" }
}
```

```json
{
  "type": "history_query",
  "payload": {
    "chain": "solana",
    "minScore": 70,
    "from": "2026-07-18T00:00:00Z",
    "to": "2026-07-19T00:00:00Z",
    "limit": 50
  }
}
```

### D.3 Error Frames

```json
{
  "type": "error",
  "payload": {
    "code": "RATE_LIMIT",
    "message": "Helius WebSocket rate limited",
    "retryAfterMs": 2000
  }
}
```

Error codes: `RATE_LIMIT`, `INSUFFICIENT_BALANCE`, `SLIPPAGE_EXCEEDED`, `EXECUTION_FAILED`, `NETWORK_ERROR`, `INVALID_SIGNATURE`.

---

## Appendix E: Frontend Component Specifications

### E.1 Component Tree

```
src/
├── components/
│   ├── layout/
│   │   ├── AppShell.tsx
│   │   ├── Sidebar.tsx
│   │   ├── StatusBar.tsx
│   │   └── Footer.tsx
│   ├── scan/
│   │   ├── ScanPipeline.tsx
│   │   ├── WorkerCard.tsx
│   │   └── QueueDepth.tsx
│   ├── market/
│   │   ├── MarketOverview.tsx
│   │   ├── TopMovers.tsx
│   │   └── TrendHeatmap.tsx
│   ├── feed/
│   │   ├── LiveFeed.tsx
│   │   ├── TokenCard.tsx
│   │   └── RugAlertModal.tsx
│   ├── analysis/
│   │   ├── AnalysisPanel.tsx
│   │   ├── ScoreGauge.tsx
│   │   ├── ProfitIntelligence.tsx
│   │   └── RiskMatrix.tsx
│   ├── trade/
│   │   ├── TradeTerminal.tsx
│   │   ├── SlippagePreview.tsx
│   │   └── GasEstimator.tsx
│   ├── history/
│   │   ├── HistoryExplorer.tsx
│   │   ├── SearchBar.tsx
│   │   └── CalendarPicker.tsx
│   └── performance/
│       ├── PerformanceDashboard.tsx
│       ├── WinRateGauge.tsx
│       ├── PnLChart.tsx
│       └── DrawdownChart.tsx
├── hooks/
│   ├── useWebSocket.ts
│   ├── useTokenHistory.ts
│   └── useTradeExecutor.ts
├── stores/
│   ├── scanStore.ts
│   ├── tokenStore.ts
│   ├── tradeStore.ts
│   └── systemStore.ts
└── types/
    ├── messages.ts
    ├── analysis.ts
    └── trade.ts
```

### E.2 Key Component Specs

#### ScanPipeline

```
Props: none
State: workers[], queueDepth, apiLatencyMs
Updates: via WS "system_status" every 1s
Visual: 8 worker cards in 2x4 grid. Each shows:
  - Status indicator (green/yellow/red)
  - Source feed name
  - Requests/min
  - Error rate (last 60s)
Queue depth: progress bar, color shifts yellow >500, red >1000
```

#### LiveFeed

```
Props: maxItems (default 50)
State: items[]
Updates: via WS "token_surfaced", prepend to array, cap at maxItems
Item layout:
  [Score gauge (radial)] [Symbol | Chain | DEX] [Price] [MCap] [Liq]
  [Bullish signals (collapsible)] [Profit targets]
Color coding:
  score >= 85: red pulse animation
  score >= 75: magenta
  score >= 65: green
Auto-scroll: enabled by default, pause on hover
```

#### RugAlertModal

```
Trigger: WS "rug_alert" with riskTier = "CRITICAL"
Behavior:
  - Full-screen overlay
  - Red pulsing border (CSS animation, 1s cycle)
  - Siren sound (optional, user-toggleable)
  - Displays risk vectors in 2x2 grid:
    [Contract] [Liquidity]
    [Developer] [Social]
  - Each cell: icon + flag list + severity badge
  - Action buttons: [Dismiss] [View Token] [Add to Blacklist]
Sound: data:audio/wav;base64,... embedded or user-provided
```

#### TradeTerminal

```
Props: token (TokenSummary)
State: sizeUsd, slippageBps, estimatedOutput, gasUsd, loading
Flow:
  1. User enters size (USD) or selects preset (10/50/100/500)
  2. Component fetches quote via REST /v1/quote
  3. Displays: expected output, price impact%, gas estimate, route
  4. User confirms → emits WS "manual_trade"
  5. Polls for confirmation, shows tx status
Slippage: dropdown 0.5% / 1% / 2% / 3%, locked per token tier
```

#### ScoreGauge

```
Props: score (0-100)
Implementation: SVG radial gauge
  - Background arc: dark gray
  - Foreground arc: gradient green→yellow→red based on score
  - Center: score number (large) + tier label (small)
Animation: transition stroke-dashoffset 500ms ease-out
Threshold lines at 60, 65, 75, 85
```

### E.3 State Management (Zustand)

```typescript
// stores/scanStore.ts
interface ScanStore {
  workers: Worker[];
  queueDepth: number;
  apiLatencyMs: number;
  setStatus: (payload: SystemStatusPayload) => void;
}

// stores/tokenStore.ts
interface TokenStore {
  surfaced: TokenSummary[];
  alerts: RugAlert[];
  addToken: (t: TokenSummary) => void;
  addAlert: (a: RugAlert) => void;
}

// stores/tradeStore.ts
interface TradeStore {
  trades: Trade[];
  positions: Position[];
  executeTrade: (req: TradeRequest) => Promise<TradeResult>;
}

// stores/systemStore.ts
interface SystemStore {
  mode: 'auto' | 'draft' | 'manual';
  pnlToday: number;
  tradesToday: number;
  setMode: (mode: SystemMode) => void;
}
```

---

## Appendix F: Testing Strategy

### F.1 Test Pyramid

```
        /\
       /E2E\        10% — Cypress / Playwright (UI + API flows)
      /─────\
     /Integr\       30% — Go tests against real Postgres + Redis
    /────────\
   /Unit Tests\    60% — Pure function tests (scorer, gates, rug detect)
  /────────────\
```

### F.2 Unit Tests (Go)

| Package | Test File | Coverage Target | Key Tests |
|---------|-----------|-----------------|-----------|
| `engine/scorer` | `scorer_test.go` | 95% | All gate boundaries, score edge cases |
| `engine/rugdetect` | `contract_test.go` | 90% | Mint authority, freeze authority, bytecode patterns |
| `engine/rugdetect` | `liquidity_test.go` | 90% | LP lock parsing, concentration thresholds |
| `engine/rugdetect` | `developer_test.go` | 85% | Wallet clustering heuristics, sell detection |
| `engine/rugdetect` | `social_test.go` | 80% | Bot pattern detection, sentiment scoring |
| `exec/jupiter` | `jupiter_test.go` | 85% | Quote parsing, error handling, retry logic |
| `exec/zero_x` | `zero_x_test.go` | 85% | Quote parsing, chain routing |
| `db/postgres` | `postgres_test.go` | 90% | Schema migrations, CRUD, full-text search |
| `ws/hub` | `hub_test.go` | 85% | Client lifecycle, broadcast, channel routing |

### F.3 Property-Based Tests

For the scoring engine, use Go `testing/quick` or `rapid`:

```go
func TestScoreBounds(t *testing.T) {
    property := func(volAccel, buyPct, momentum float64) bool {
        p := &fixtures.RandomPair(volAccel, buyPct, momentum)
        a, err := Scorer.Score(context.Background(), p)
        if err != nil {
            return true // rejection is valid
        }
        return a.Score >= 0 && a.Score <= 100
    }
    if err := rapid.Check(t, property); err != nil {
        t.Error(err)
    }
}
```

### F.4 Integration Tests

```go
// Postgres + Redis + NATS stack via testcontainers
func TestEndToEndScoring(t *testing.T) {
    // 1. Insert fixture token + pair_snapshot into Postgres
    // 2. Publish pair event to NATS
    // 3. Verify scoring engine consumes, scores, writes analyses row
    // 4. Verify WS hub broadcasts "token_surfaced" to connected clients
    // 5. Verify Redis cache hit on second identical event
}
```

### F.5 Load Tests

| Scenario | Target | Tool |
|----------|--------|------|
| 1000 pairs/sec ingestion | p99 < 50ms | k6 WebSocket |
| 100 concurrent WS clients | p99 < 100ms | k6 |
| 10,000 score evaluations/sec | p99 < 20ms | Go benchmark |
| Postgres write throughput | >5000 rows/sec | pgbench |
| Redis pub/sub latency | p99 < 5ms | redis-benchmark |

### F.6 Chaos Tests

- Kill NATS server during scan → verify circuit breaker opens, workers halt gracefully
- Partition Postgres → verify queue buffers in Redis, resume on reconnect
- Malformed WebSocket message → verify hub closes connection, logs error
- OOM on scoring worker → verify supervisor restarts, no data loss

---

## Appendix G: Performance Benchmarks

### G.1 Baseline Targets (Go)

| Operation | Target | Critical | Measurement |
|-----------|--------|----------|-------------|
| Single pair scoring | < 5ms | < 10ms | `go test -bench=BenchmarkScore` |
| 1000 pairs batch | < 200ms | < 400ms | `go test -bench=BenchmarkBatchScore` |
| Postgres insert (analyses) | < 3ms | < 10ms | `go test -bench=BenchmarkDBInsert` |
| Redis pub/sub round-trip | < 2ms | < 5ms | `go test -bench=BenchmarkRedisPubSub` |
| WebSocket broadcast (100 clients) | < 10ms | < 20ms | `go test -bench=BenchmarkWSBroadcast` |
| Jupiter quote fetch | < 50ms | < 100ms | `go test -bench=BenchmarkJupiterQuote` |
| Helius account parse | < 1ms | < 5ms | `go test -bench=BenchmarkHeliusParse` |

### G.2 Benchmark Harness

```go
// engine/scorer/bench_test.go
func BenchmarkScore(b *testing.B) {
    scorer := NewScorer()
    pair := fixtures.NewTypicalPair()
    b.ResetTimer()
    for i := 0; i < b.N; i++ {
        _, _ = scorer.Score(context.Background(), pair)
    }
}

func BenchmarkBatchScore(b *testing.B) {
    scorer := NewScorer()
    pairs := fixtures.NewBatch(1000)
    b.ResetTimer()
    for i := 0; i < b.N; i++ {
        for _, p := range pairs {
            _, _ = scorer.Score(context.Background(), p)
        }
    }
}
```

Run: `go test -bench=. -benchmem -cpu=4 ./engine/scorer/`

### G.3 Continuous Benchmarking

Use `benchstat` to track regressions in CI:

```yaml
# .github/workflows/bench.yml
- name: Run benchmarks
  run: go test -bench=. -benchmem ./... > bench.txt
- name: Compare
  run: benchstat bench.txt
```

Alert if any benchmark degrades > 10% from baseline.

---

## Appendix H: Configuration & Secrets Management

### H.1 Configuration Schema (Viper / YAML)

```yaml
# config/default.yaml
app:
  name: "dexscannerbot"
  version: "4.0.0"
  mode: "auto"  # auto | draft | manual
  environment: "production"

scanner:
  workers: 8
  batch_size: 30
  poll_interval_ms: 1000
  gatekeep_score: 60
  max_reject_log: 1000

chains:
  solana:
    enabled: true
    rpc: "https://api.mainnet-beta.solana.com"
    ws: "wss://api.mainnet-beta.solana.com"
    commitment: "confirmed"
  ethereum:
    enabled: false
    rpc: "https://eth-mainnet.alchemyapi.io/v2/..."
    ws: "wss://eth-mainnet.ws.alchemyapi.io/v2/..."
  base:
    enabled: false
    rpc: "https://base-mainnet.alchemyapi.io/v2/..."
    ws: "wss://base-mainnet.ws.alchemyapi.io/v2/..."

datasources:
  dexscreener:
    enabled: true
    poll_interval_ms: 5000
  birdeye:
    enabled: true
    api_key: "${BIRDEYE_API_KEY}"
    ws_endpoint: "wss://public-api.birdeye.so/solana/token"
  helius:
    enabled: true
    api_key: "${HELIUS_API_KEY}"
    geyser_endpoint: "wss://geyser.mainnet.solana.shyft.to?"
  alchemy:
    enabled: true
    api_key: "${ALCHEMY_API_KEY}"

execution:
  solana:
    provider: "jupiter"  # jupiter | raydium
    jupiter_ultra_url: "https://ultra-api.jup.ag/ultra/v1/order"
    max_slippage_bps: 300
    priority_fee_lamports: 5000
    retry_attempts: 3
    retry_backoff_ms: 200
  evm:
    provider: "0x"  # 0x | uniswapx
    zero_x_api: "https://api.0x.org/swap/v1/quote"
    max_slippage_bps: 300
    deadline_seconds: 30

position:
  partial_tp_1_pct: 25
  partial_tp_1_mult: 2.0
  partial_tp_2_pct: 50
  partial_tp_2_mult: 5.0
  trail_to_breakeven: true
  max_hold_hours_auto: 4
  max_hold_hours_manual: 24
  stop_loss_pct: -30
  trailing_stop_pct: -15

database:
  host: "${DB_HOST}"
  port: 5432
  name: "dexscannerbot"
  user: "${DB_USER}"
  password: "${DB_PASSWORD}"
  sslmode: "require"
  pool_max: 20
  pool_idle: 30

redis:
  addr: "${REDIS_ADDR}"
  password: "${REDIS_PASSWORD}"
  db: 0
  pool_size: 50

nats:
  urls: ["${NATS_URL}"]
  user: "${NATS_USER}"
  password: "${NATS_PASSWORD}"

wallet:
  address: "${WALLET_ADDRESS}"
  private_key: "${WALLET_PRIVATE_KEY}"  # encrypted at rest
  max_balance_usd: 500
  daily_withdraw_limit_usd: 200

frontend:
  host: "0.0.0.0"
  port: 3000
  ws_path: "/ws"
  api_path: "/api/v1"

logging:
  level: "info"  # debug | info | warn | error
  format: "json"
  output: "stdout"

monitoring:
  prometheus_port: 9090
  grafana_url: "http://localhost:3001"
  alertmanager_url: "http://localhost:9093"
```

### H.2 Secrets Injection

```bash
# .env (never committed)
export HELIUS_API_KEY="abc123..."
export ALCHEMY_API_KEY="def456..."
export BIRDEYE_API_KEY="ghi789..."
export DB_PASSWORD="jkl012..."
export REDIS_PASSWORD="mno345..."
export NATS_PASSWORD="pqr678..."
export WALLET_PRIVATE_KEY="stu901..."  # AES-256-GCM encrypted

# age encryption for repo storage
age-encrypt -r <pubkey> wallet.key > wallet.key.age
```

### H.3 Rotation Schedule

| Secret | Rotation | Method |
|--------|----------|--------|
| Helius API key | 30 days | Auto via Helius dashboard → update Vault |
| Alchemy API key | 30 days | Same |
| Birdeye API key | 30 days | Same |
| DB credentials | 90 days | `pg_rotate_password` + Vault update |
| Wallet private key | 7 days | Generate new keypair, migrate balance, update Vault |
| TLS certificates | 90 days | cert-manager / Let's Encrypt |

### H.4 Runtime Configuration Reload

```go
// Use Viper with on-demand reload
func LoadConfig() (*Config, error) {
    viper.SetConfigName("default")
    viper.SetConfigType("yaml")
    viper.AddConfigPath("./config")
    viper.AutomaticEnv()
    viper.SetEnvKeyReplacer(strings.NewReplacer(".", "_"))
    
    if err := viper.ReadInConfig(); err != nil {
        return nil, err
    }
    
    // Watch for file changes in dev
    viper.WatchConfig()
    viper.OnConfigChange(func(e fsnotify.Event) {
        log.Info("Config reloaded", "file", e.Name)
    })
    
    var cfg Config
    if err := viper.Unmarshal(&cfg); err != nil {
        return nil, err
    }
    return &cfg, nil
}
```

### H.5 Security Checklist

- [ ] All secrets in Vault / env vars, never in repo
- [ ] Private key encrypted at rest, decrypted only in memory
- [ ] TLS 1.3 for all outbound connections
- [ ] mTLS for Helius and Alchemy enterprise endpoints
- [ ] Request signing for exchange APIs
- [ ] Input validation on all external data
- [ ] Rate limiting per API key
- [ ] Audit logging for all trade executions
- [ ] Daily wallet balance alerts
- [ ] Weekly key rotation reminder in monitoring dashboard
