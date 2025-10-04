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

© 2025 — TP-WSRTD-Client (Open Integration Project)
