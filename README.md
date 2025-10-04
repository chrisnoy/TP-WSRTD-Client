# TP-WSRTD-Client

### Tai Pan → AmiBroker WebSocket Bridge (Feed + Client)

This repository contains the client-side bridge and feed server that integrate the **Tai Pan Real-Time API (TPRAccess)** with the **AmiBroker WS_RTD plugin**.

The project provides:
- 🔁 **Real-time streaming quotes** (`Json-RTD`)  
- 📈 **Full historical backfill** (`Json-HIST`) using chronological 5-day chunking  
- 🧩 **INFO metadata injection** for each symbol (Full Name, Alias, Address, Market, ISIN)  
- 🧮 **Prev Close fix** for the Tai Pan feed (adds `pc` field missing from TPRAccess RTQ feed)  
- 🕰️ **Chronological backfill order** to prevent chart gaps or overwrite issues  
- 🧠 **Automatic bfauto completion** when new bars appear intraday  

---

## Components

### 1️⃣ `feed.cs` – Tai Pan → Bridge WebSocket Feed Server
- Connects to **TPRAccess** via `TPRServerConnection`
- Subscribes to real-time quotes and broadcasts JSON ticks to WebSocket clients
- Handles backfill commands (`bffull`, `bfauto`, `bfall`, `bfsym`)
- Returns native 1-minute bars in `dtohlcvi` format  
- Adds missing **Prev Close**, **Day Open**, **High**, **Low**, and **Volume** to each RTQ packet

### 2️⃣ `bridge.py` – Bridge ↔ WS_RTD Client
- Connects between **feed server (port 10103)** and **WS_RTD plugin (port 10101)**
- Passes through real-time quotes to AmiBroker
- Converts raw ticks into 1-minute OHLCV bars for HIST mode
- Loads and sends symbol metadata (INFO) from `coretest.csv`
- Ensures large backfills (> 9000 bars) send only the oldest range so bfauto can fill the rest

---

## INFO Metadata Format (`coretest.csv`)

| Symbol | FullName | Alias | Address | Market | ISIN |
|:-------|:----------|:------|:---------|:--------|:------|
| AB.FR  | ABB SA (Euronext Paris) | ABB.PA | Euronext | 1 | FR0000123456 |

All fields are optional, but **Market** should be an integer (0–3) for AmiBroker’s internal `im` field.

---

## Usage Overview

1. Start `feed.cs` (C# console app)  
2. Start `bridge.py` (Python 3.10+)  
3. Open AmiBroker → RT Data plugin → Connect (green light)  
4. Use **Retrieve** button in AmiBroker Symbol → Information to import INFO metadata  
5. Backfill (`bffull`) then run live (`bfauto`) – all data should now sync chronologically

---

## Tested Environment
- **Windows 10 x64**
- **Tai Pan Realtime v8.x SDK (TPRAccess.dll)**
- **AmiBroker v6.50+**
- **Python 3.10 / websockets 12.x**
- **.NET Framework 4.8**

---

## Notes
> ⚠️ This repository contains *client-side integration only*.  
> The Tai Pan SDK and AmiBroker WS_RTD plugin are proprietary and must be licensed separately.  
> This code is provided for educational and testing purposes under the MIT license.

---
⚙️ Setup Overview
🧩 System Architecture

The TP-WSRTD-Client consists of two key components:

C# Feed Server (FeedServer.cs)

Connects to Tai Pan Realtime (TPRAccess).

Streams real-time ticks and handles historical backfill (Json-HIST) requests.

Supplies missing Previous Close values from snapshot data (since Tai Pan does not provide this in the live feed).

Supports AmiBroker WS-RTD backfill commands (bffull, bfauto, etc.) with proper chronological chunking.

Python Bridge (bridge.py)

Links the Feed Server with the AmiBroker WS-RTD Plugin.

Forwards real-time quotes (Json-RTD) directly.

Aggregates tick history into 1-minute OHLCV bars for backfill.

Implements INFO metadata injection from a CSV file (Company Name, Alias, ISIN, Market).

Loads up to 173 symbols from cold start, populating AmiBroker’s database automatically.
---
Tai Pan Realtime (TPRAccess)
        │
        ▼
 C# Feed Server  →  WebSocket (port 10103)
        │
        ▼
 Python Bridge  →  Relay (port 10101)
        │
        ▼
 AmiBroker WS-RTD Plugin
---

🧠 Key Features

Chronological backfill using 5-day chunks via GetIntradayChart.

Auto-fill for recent data through bfauto when AmiBroker reopens.

Previous Close (pc) injection for correct breakout calculations.

INFO JSON populates AmiBroker symbol metadata (Full Name, Alias, Market Code, ISIN).

Stable RTQ passthrough (no aggregation or delay).

---

🧾 Typical Workflow

Start the Feed Server:

TPFeedServer.exe


Launch the Bridge:

python bridge.py


Open AmiBroker → connect the WS-RTD Plugin.

Symbols appear automatically with INFO metadata.

Historical data (up to 30 days) backfills on demand.

---

⚠️ Known Limitations / Notes for Testers

📅 Backfill (HIST)

The Feed Server retrieves 30 days of 1-minute bars using GetIntradayChart() in 5-day chunks.

Only the oldest 9000 bars are sent initially (to avoid AmiBroker overwriting recent data).

AmiBroker automatically issues bfauto to fill the remainder after startup.

bfauto will only execute if a valid timestamp is provided in the request.

🧾 INFO Metadata

INFO JSON is sent at startup and whenever AmiBroker triggers any bf* event.

Market field is numeric (integer), as required by AmiBroker (im field).

CSV headers must include:

Symbol,SymbolNo,FullName,Alias,Address,Market,ISIN

Metadata populates AmiBroker’s Full Name, Alias, Address, ISIN, and Market columns automatically.

💹 Previous Close Handling

Tai Pan’s realtime feed does not supply a PrevClose field per tick.

The Feed Server injects this value from the symbol snapshot, ensuring correct %-change and breakout logic in AmiBroker.

🔁 Restart Behavior

On cold start, the Bridge sends INFO for all symbols automatically.

Restarting AmiBroker will trigger bfauto updates, filling missing bars.

Real-time ticks continue seamlessly from the Feed Server once the Bridge reconnects.

📡 Stability

WebSocket frame size limit (≈ 1 MB) is enforced.

Large payloads are split automatically or trimmed to prevent 1009 message too big errors.

Both Relay and Feed operate with ping_interval=None to avoid spurious disconnects.

© 2025 — TP-WSRTD-Client (Open Integration Project)
