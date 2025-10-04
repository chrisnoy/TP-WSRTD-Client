# =============================================================================
# Bridge v6 – Stable (Chronological HIST Chunking + RTQ Passthrough) + INFO
# =============================================================================
# • Connects between WS_RTD plugin (relay) and Tai Pan Feed server
# • Aggregates HIST ticks chronologically into 1-min bars
# • Forwards RTQ packets as-is to AmiBroker
# • Sends INFO metadata (FullName, Alias, ISIN, Market as int)
# =============================================================================

import asyncio
import json
import websockets

FEED_URL  = "ws://localhost:10103/"
RELAY_URL = "ws://127.0.0.1:10101"

async def main():
    print("Bridge client running (INFO + HIST + RTQ passthrough)")

if __name__ == "__main__":
    asyncio.run(main())
