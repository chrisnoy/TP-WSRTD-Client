# =============================================================================
# Bridge_1210e.py  —  Live Feed Edition + Auto bfauto gap-fill
# =============================================================================
# ✅ Based on 1210d.py
# ✅ Detects 10K> truncations and automatically sends a bfauto continuation
# ✅ Gap trigger: if last bar older than 30 minutes (Δend>30min)
# ✅ Feed still ignores bfauto-without-params
# =============================================================================

import asyncio
import json
import csv
from datetime import datetime, timezone, timedelta
import websockets

FEED_URL  = "ws://localhost:10103/"   # FeedServer
RELAY_URL = "ws://127.0.0.1:10101"    # Relay / WS_RTD
LOG_FILE  = "bridge_live.log"
CSV_PATH  = "C:/Users/dexte/OneDrive/Desktop/addons/coretest.csv"


# -------------------- Logging --------------------
def log(msg: str):
    print(msg)
    #try:
    #    with open(LOG_FILE, "a", encoding="utf-8") as f:
    #        f.write(msg + "\n")
    #except Exception:
    #    pass


# -------------------- INFO Loader --------------------
def load_info_from_csv(csv_path):
    info_list = []
    try:
        with open(csv_path, newline='', encoding='utf-8-sig') as f:
            reader = csv.DictReader(f)
            for row in reader:
                sym = (row.get("Symbol") or "").strip()
                if not sym:
                    continue
                info_list.append({
                    "n": sym,
                    "fullname": (row.get("FullName") or "").strip(),
                    "alias": (row.get("Alias") or "").strip(),
                    "address": (row.get("Address") or "").strip(),
                    "market": int((row.get("Market") or "0").strip() or 0),
                    "isin": (row.get("ISIN") or "").strip()
                })
        log(f"[bridge] INFO CSV loaded: {len(info_list)} symbols")
    except Exception as e:
        log(f"[bridge] ERROR loading INFO CSV: {e}")
    return info_list


# -------------------- HIST aggregation --------------------
def is_valid_dt(d: int, ti: int) -> bool:
    if d < 19000101 or d > 20991231:
        return False
    if ti < 0 or ti > 235959:
        return False
    mm = (ti // 100) % 100
    ss = ti % 100
    return mm <= 59 and ss <= 59


def aggregate_hist_ticks(sym: str, ticks: list) -> list:
    agg, bad = {}, 0
    for t in sorted(ticks, key=lambda x: (int(x[0]), int(x[1]))):
        try:
            d, ti, o, h, l, c, v, _ = t
            if not is_valid_dt(int(d), int(ti)):
                continue
            ts = datetime.strptime(f"{int(d):08d}{int(ti):06d}", "%Y%m%d%H%M%S")
            key = ts.strftime("%Y%m%d%H%M")
            px, vol = float(c), float(v or 0)
            if key not in agg:
                agg[key] = {"d": int(d), "t": ts.hour * 10000 + ts.minute * 100,
                            "o": px, "h": px, "l": px, "c": px, "v": vol}
            else:
                bar = agg[key]
                bar["h"] = max(bar["h"], px)
                bar["l"] = min(bar["l"], px)
                bar["c"] = px
                bar["v"] += vol
        except Exception:
            bad += 1
    bars = [[b["d"], b["t"], b["o"], b["h"], b["l"], b["c"], b["v"], 0] for b in agg.values()]
    bars.sort(key=lambda x: (x[0], x[1]))
    if bars:
        log(f"[HIST] {sym} aggregated {len(bars)} bars")
    if bad:
        log(f"[HIST] {sym} skipped malformed ticks: {bad}")
    return bars


# -------------------- Core --------------------
async def run_once():
    log("[bridge] connecting to relay and feed...")

    info_list = load_info_from_csv(CSV_PATH)
    info_sent = False
    info_triggered = asyncio.Event()

    async with websockets.connect(RELAY_URL, ping_interval=None, ping_timeout=None, max_size=None) as relay_ws, \
               websockets.connect(FEED_URL,  ping_interval=None, ping_timeout=None, max_size=None) as feed_ws:
        await relay_ws.send("rolesend")
        await relay_ws.recv()
        await relay_ws.send("rolerecv")
        log("[relay] handshake complete")
        log("[feed] connected.")

        rtq_count = 0

        # --- Feed listener ---
        async def listen_feed():
            nonlocal rtq_count
            async for message in feed_ws:
                if message == "ping":
                    await feed_ws.send("pong")
                    continue
                if not message:
                    continue
                try:
                    data = json.loads(message)
                except Exception:
                    continue

                # HIST payloads
                if isinstance(data, dict) and "hist" in data:
                    sym = data.get("hist")
                    ticks = data.get("bars", [])
                    bars = aggregate_hist_ticks(sym, ticks)
                    total = len(bars)
                    if total > 10000:
                        bars = bars[:10000]
                        log(f"[HIST] {sym}: {total}→10000 oldest only")

                    # --- Range log + auto gap fill ---
                    if bars:
                        first_d, first_t = bars[0][0], bars[0][1]
                        last_d, last_t = bars[-1][0], bars[-1][1]
                        first_dt = datetime.strptime(f"{first_d:08d}{first_t:06d}", "%Y%m%d%H%M%S")
                        last_dt  = datetime.strptime(f"{last_d:08d}{last_t:06d}", "%Y%m%d%H%M%S")
                        tz = timezone(timedelta(hours=1))  # CET/CEST
                        now_paris = datetime.now(tz)
                        today_open = now_paris.replace(hour=9, minute=0, second=0, microsecond=0)
                        delta_min = abs((last_dt - today_open.replace(tzinfo=None)).total_seconds()) / 60
                        tag = "[OK]" if delta_min <= 5 else "[WARN]"
                        log(f"{tag} {sym}: {len(bars)} bars ({first_dt:%Y-%m-%d %H:%M} → {last_dt:%Y-%m-%d %H:%M}) Δend={delta_min:.1f}min from 09:00CET")

                        # --- AUTO CONTINUATION ---
                        try:
                            gap_min = (now_paris - last_dt.replace(tzinfo=tz)).total_seconds() / 60
                            if total >= 10000 and gap_min > 30:
                                bfauto_msg = {"cmd": "bfauto", "arg": f"{sym} {last_d} {last_t}"}
                                await feed_ws.send(json.dumps(bfauto_msg))
                                log(f"[bridge] gap-fill triggered for {sym} from {last_d} {last_t} (gap {gap_min:.1f} min)")
                        except Exception as e:
                            log(f"[bridge] auto-bfauto failed for {sym}: {e}")
                    else:
                        log(f"[WARN] {sym}: empty HIST payload")

                    # Forward HIST to relay
                    await relay_ws.send(json.dumps({"hist": sym, "format": "dtohlcvi", "bars": bars}, separators=(",", ":")))
                    continue

                # RTQ
                if isinstance(data, list):
                    valid = [t for t in data if isinstance(t, dict) and t.get("n")]
                    if valid:
                        rtq_count += len(valid)
                        await relay_ws.send(json.dumps(valid, separators=(",", ":")))
                elif isinstance(data, dict) and "n" in data:
                    rtq_count += 1
                    await relay_ws.send(json.dumps([data], separators=(",", ":")))

        # --- Relay listener ---
        async def listen_relay():
            async for raw in relay_ws:
                try:
                    msg = json.loads(raw)
                except Exception:
                    continue
                cmd = msg.get("cmd")
                if cmd in ("bfauto", "bffull", "bfall", "bfsym"):
                    if not info_triggered.is_set():
                        info_triggered.set()
                    log(f"[relay] backfill request → {cmd} {msg.get('arg','')}")
                    await feed_ws.send(json.dumps(msg))
                elif cmd == "cping":
                    await relay_ws.send(json.dumps({"cmd": "cpong", "arg": ""}))

        # --- Feed monitor ---
        async def feed_monitor():
            nonlocal rtq_count
            while True:
                await asyncio.sleep(10)
                log(f"[feed] RTQ {rtq_count} msgs in last 10s")
                rtq_count = 0

        # --- INFO broadcaster ---
        async def info_broadcaster():
            await info_triggered.wait()
            await asyncio.sleep(0.5)
            if not info_sent and info_list:
                for row in info_list:
                    msg = {
                        "info": row.get("n"),
                        "fn": row.get("fullname"),
                        "an": row.get("alias"),
                        "ad": row.get("address"),
                        "wi": row.get("isin"),
                        "im": row.get("market")
                    }
                    await relay_ws.send(json.dumps(msg, separators=(",", ":")))
                    await asyncio.sleep(0.005)
                log(f"[bridge] INFO broadcast sent ({len(info_list)} symbols)")

        await asyncio.gather(listen_feed(), listen_relay(), feed_monitor(), info_broadcaster())


async def main():
    log("[bridge] starting Bridge_1210e (auto bfauto gap-fill)...")
    while True:
        try:
            await run_once()
        except Exception as e:
            log(f"[main] disconnected: {e}")
            await asyncio.sleep(3)


if __name__ == "__main__":
    asyncio.run(main())
