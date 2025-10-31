// ============================================================================
//  FeedServer_1210_fixed_final_merged.cs
//  ---------------------------------------------------------------------------
//  ✅ Stable + Safe Send
//  ✅ Paris-aligned bffull/bfauto window logic
//  ✅ Defines AddTrade, RunWebSocketServer, StartOneMinuteAggregator
//  ✅ Trade cache uses Paris-local timestamps for clean 1m bars
//  ✅ C# 7.3 compatible
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TPRAccess;

class Program
{
    // ------------------------ FIELDS ------------------------
    private static Dictionary<string, int> Symbols;
    private static TPRServerConnection conn;
    private static bool generatorMode = false;
    private static readonly Random rng = new Random();
    private static readonly Dictionary<string, DateTime> LastFullEndUtc =
        new Dictionary<string, DateTime>();

    // trade cache: Paris-local timestamps
    private static readonly ConcurrentDictionary<string, List<(DateTime t, double p, int v)>> tradeCache
        = new ConcurrentDictionary<string, List<(DateTime, double, int)>>();

    // per-symbol bar state for the 1m flusher
    private class BarState
    {
        public double Open, High, Low, Close;
        public long Volume;
        public DateTime Start;
        public bool Initialized;
    }
    private static readonly ConcurrentDictionary<string, BarState> barStates =
        new ConcurrentDictionary<string, BarState>();
      // --- selective debug symbols for trade/agg tracing ---
private static readonly HashSet<string> DebugSymbols = new HashSet<string>
{
    "KPN.NL",     // your main test symbol
    "SHELL.NL",    // optional second test symbol
	"ADYEN.NL"
};
    
	// ------------------------ ENTRY POINT ------------------------
    static async Task Main()
    {
        try
        {
            Symbols = LoadSymbols(@"C:\Users\dexte\OneDrive\Desktop\addons\coretest.csv");

            conn = new TPRServerConnection("90922ec3-5e6b-4821-841a-9bd8cb3ef444");
            if (!conn.Login("883013", "Epidavros45"))
            {
                Console.WriteLine("Login failed.");
                return;
            }
            Console.WriteLine("Logged in successfully.");

            var idToTicker = Symbols.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
            var router = new FeedRouter(idToTicker);

            // --- Initial snapshot ---
            var snapshots = conn.GetSymbols(new List<int>(Symbols.Values));
            foreach (var s in snapshots)
            {
                var row = new RTQRow
                {
                    SymbolId = s.SymbolNo,
                    Symbol = idToTicker.ContainsKey(s.SymbolNo) ? idToTicker[s.SymbolNo] : s.ISIN,
                    FullName = s.Name,
                    ISIN = s.ISIN,
                    WKN = Convert.ToString(s.WKN),
                    Exchange = Convert.ToString(s.Exchange),
                    Currency = Convert.ToString(s.Currency),
                    Last = s.LastTrade.Quote,
                    Bid = s.LastBid.Quote,
                    Ask = s.LastAsk.Quote,
                    PrevClose = s.PrevClose,
                    DayOpen = s.DayOpen,
                    DayHigh = s.DayHigh,
                    DayLow = s.DayLow,
                    DayVolume = (long)s.DayVolume
                };

                // --- Fallbacks when market closed / null fields ---
                if (!row.DayOpen.HasValue || row.DayOpen == 0)
                    row.DayOpen = row.Last ?? row.PrevClose ?? 10.0;
                if (!row.DayHigh.HasValue || row.DayHigh == 0)
                    row.DayHigh = row.Last ?? row.DayOpen ?? row.PrevClose ?? 10.0;
                if (!row.DayLow.HasValue || row.DayLow == 0)
                    row.DayLow = row.Last ?? row.DayOpen ?? row.PrevClose ?? 10.0;

                row.RecalcChange();
                router.SetRow(row);
            }

            _ = RunWebSocketServer(10103, router);
            StartOneMinuteAggregator(router);   // start 1-minute flusher aligned to :00

            // --- Live or generator ---
            if (!generatorMode)
            {
                var pushFeed = conn.GetPushFeed();
                pushFeed.ConnectFeed();
                pushFeed.SubscribeSymbol(new List<int>(Symbols.Values));
                Console.WriteLine("Push feed connected + subscribed.");

                Task.Run(() =>
                {
                    while (true)
                    {
                        var msg = pushFeed.GetNextPushFeedMessage();
                        if (msg == null) continue;
                        router.OnMessage(msg);
                        pushFeed.ReturnFeedMessage(msg);
                    }
                });
            }
            else
            {
                Console.WriteLine("[generator] Stress-test mode active.");
                var list = router.AllRows.Values.ToList();

                // initial snapshot so bridge sees all symbols
                await BroadcastBatch(router, "baseline");

                Task.Run(async () =>
                {
                    while (true)
                    {
                        // nudge a random subset each burst
                        int take = Math.Max(1, list.Count / 15);
                        foreach (var row in list.OrderBy(_ => rng.Next()).Take(take))
                        {
                            double anchor = row.Last ?? row.DayOpen ?? row.PrevClose ?? 10.0;
                            double drift = (rng.NextDouble() - 0.5) * 0.003; // ±0.15%
                            double newLast = Math.Max(0.01, anchor * (1.0 + drift));
                            double spr = 0.0002 + rng.NextDouble() * 0.0003;
                            double half = newLast * spr * 0.5;
                            row.Last = newLast;
                            row.Bid = Math.Max(0.01, newLast - half);
                            row.Ask = newLast + half;
                            row.BidSize = rng.Next(100, 4000);
                            row.AskSize = rng.Next(100, 4000);
                            int tradeSz = rng.Next(100, 800);
                            if (rng.NextDouble() < 0.02) tradeSz *= 5;
                            row.LastTradeVolume = tradeSz;
                            row.DayVolume = (row.DayVolume ?? 0) + tradeSz;
                            row.RecalcChange();
                        }
                        await Task.Delay(250);
                    }
                });
            }

            // --- Batch broadcasters ---
            Task.Run(async () =>
            {
                while (true)
                {
                    await BroadcastBatch(router, "live-0.9s");
                    await Task.Delay(900);
                }
            });

            Task.Run(async () =>
            {
                while (true)
                {
                    await BroadcastBatch(router, "keep-5s");
                    await Task.Delay(5000);
                }
            });

            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Fatal error: " + ex.Message);
        }
    }

    // ------------------------ HELPERS ------------------------
    private static Dictionary<string, int> LoadSymbols(string path)
    {
        var dict = new Dictionary<string, int>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            string sym = parts[0].Trim();
            if (int.TryParse(parts[1].Trim(), out int id))
                dict[sym] = id;
        }
        Console.WriteLine($"[init] Loaded {dict.Count} symbols from {path}");
        return dict;
    }

    private static async Task BroadcastBatch(FeedRouter router, string label)
    {
        try
        {
            var nowList = router.AllRows.Values.ToList();
            if (nowList.Count == 0) return;

            var arr = new List<object>(nowList.Count);
            foreach (var row in nowList)
                arr.Add(row.ToAmiJson());

            string json = JsonConvert.SerializeObject(arr);
            await WebSocketClientManager.BroadcastAsync(json);
            Console.WriteLine($"[snapshot:{label}] Sent {nowList.Count} symbols (batch)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[snapshot:{label}] ERROR: {ex.Message}");
        }
    }

    // ------------------------ WEBSOCKET SERVER (DEFINED) ------------------------
    static async Task RunWebSocketServer(int port, FeedRouter router)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:" + port + "/");
        listener.Start();
        Console.WriteLine("WebSocket server started on ws://localhost:" + port + "/");

        while (true)
        {
            var context = await listener.GetContextAsync();
            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                WebSocketClientManager.AddClient(wsContext.WebSocket);

                _ = Task.Run(async () =>
                {
                    var socket = wsContext.WebSocket;
                    var buf = new byte[4096];
                    while (socket.State == WebSocketState.Open)
                    {
                        var seg = new ArraySegment<byte>(buf);
                        var result = await socket.ReceiveAsync(seg, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var msg = Encoding.UTF8.GetString(buf, 0, result.Count);
                        try
                        {
                            var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(msg);
                            if (obj != null && obj.ContainsKey("cmd"))
                            {
                                string cmd = Convert.ToString(obj["cmd"]);
                                string arg = obj.ContainsKey("arg") ? Convert.ToString(obj["arg"]) : "";
                                if (cmd.StartsWith("bf"))
                                    await HandleBackfill(arg, cmd, socket);
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("[feed] WS parse error: " + e.Message);
                        }
                    }
                });
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    // ------------------------ 1-MINUTE AGGREGATOR (DEFINED) ------------------------
    private static void StartOneMinuteAggregator(FeedRouter router)
    {
        Thread thread = new Thread(() =>
        {
            // Align to next :00 boundary
            int delayMs = 60000 - (DateTime.Now.Second * 1000 + DateTime.Now.Millisecond);
            if (delayMs < 0 || delayMs > 60000) delayMs = 0;
            Thread.Sleep(delayMs);

            while (true)
            {
                try
                {
                    FlushOneMinuteBars(router);
                    Thread.Sleep(60000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[bars-1m] ERROR: " + ex.Message);
                    Thread.Sleep(1000);
                }
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private static void FlushOneMinuteBars(FeedRouter router)
    {
        try
        {
            var barsToSend = new List<Dictionary<string, object>>();
            var parisTz = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
            DateTime nowParis = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, parisTz);
            DateTime prevMinute = nowParis.AddMinutes(-1);

            foreach (var kvp in router.AllRows)
            {
                var row = kvp.Value;
                if (row.Last == null || row.Last <= 0) continue;

                if (!tradeCache.TryGetValue(row.Symbol, out var trades))
                    trades = new List<(DateTime, double, int)>();

                DateTime windowStart = prevMinute;
                DateTime windowEnd = prevMinute.AddMinutes(1);
                List<(DateTime t, double p, int v)> tradesThisMinute;

                lock (trades)
                {
                    tradesThisMinute = trades.Where(tt => tt.t >= windowStart && tt.t < windowEnd).ToList();
                    trades.RemoveAll(tt => tt.t >= windowStart && tt.t < windowEnd);
                }

                if (!barStates.TryGetValue(row.Symbol, out var bar))
                {
                    bar = new BarState
                    {
                        Open = row.Last.Value,
                        High = row.Last.Value,
                        Low = row.Last.Value,
                        Close = row.Last.Value,
                        Volume = 0,
                        Start = prevMinute,
                        Initialized = true
                    };
                    barStates[row.Symbol] = bar;
                }

                if (tradesThisMinute.Count > 0)
                {
                    bar.Open = tradesThisMinute.First().p;
                    bar.Close = tradesThisMinute.Last().p;
                    bar.High = tradesThisMinute.Max(x => x.p);
                    bar.Low = tradesThisMinute.Min(x => x.p);
                    bar.Volume = tradesThisMinute.Sum(x => (long)x.v);
                }
                else
                {
                    bar.Volume = 0; // no trades
                }

                if (bar.Start.Minute != nowParis.Minute)
{
    // --- WICK / DASH FIX: prevent dashed candles on no-trade minutes ---
    if (bar.High == bar.Low || Math.Abs(bar.Open - bar.Close) < (bar.Close * 0.0001))
    {
        const double body = 0.005;   // small candle body ~0.5%
        const double wick = body * 0.1;
        double mid = (bar.Open + bar.Close) * 0.5;

        if (bar.Close >= bar.Open)
        {
            bar.Open = mid - body * 0.5;
            bar.Close = mid + body * 0.5;
            bar.High = bar.Close + wick;
            bar.Low = bar.Open;
        }
        else
        {
            bar.Open = mid + body * 0.5;
            bar.Close = mid - body * 0.5;
            bar.High = bar.Open;
            bar.Low = bar.Close - wick;
        }

        Console.WriteLine($"[flat-fix] {nowParis:HH:mm:ss} {row.Symbol} placeholder applied");
    }

    
                    
					// stamp closed minute (use :59 to show close)
                    var closedMinute = new DateTime(
                        bar.Start.Year, bar.Start.Month, bar.Start.Day,
                        bar.Start.Hour, bar.Start.Minute, 59, bar.Start.Kind);

                    int d = closedMinute.Year * 10000 + closedMinute.Month * 100 + closedMinute.Day;
                    int ti = closedMinute.Hour * 10000 + closedMinute.Minute * 100 + closedMinute.Second;

                    var rec = new Dictionary<string, object>
                    {
                        ["n"]  = row.Symbol,
                        ["d"]  = d,
                        ["t"]  = ti,
                        ["o"]  = bar.Open,
                        ["h"]  = bar.High,
                        ["l"]  = bar.Low,
                        ["c"]  = bar.Close,
                        ["v"]  = bar.Volume,
                        ["s"]  = row.DayVolume ?? 0,
                        ["pc"] = row.PrevClose ?? 0.0,
                        ["do"] = row.DayOpen ?? 0.0,
                        ["dh"] = row.DayHigh ?? 0.0,
                        ["dl"] = row.DayLow ?? 0.0,
                        ["bp"] = row.Bid ?? 0.0,
                        ["ap"] = row.Ask ?? 0.0,
                        ["bs"] = row.BidSize,
                        ["as"] = row.AskSize
                    };
                    // Optional per-minute debug summary for selected symbols
if (DebugSymbols.Contains(row.Symbol))
    Console.WriteLine($"[agg] {row.Symbol,-10} minute={prevMinute:HH:mm} trades={tradesThisMinute.Count,3} vol={bar.Volume,8}");
                    barsToSend.Add(rec);

                    // reset for next minute
                    bar.Open = bar.High = bar.Low = bar.Close = row.Last.Value;
                    bar.Volume = 0;
                    bar.Start = nowParis;
                }
            }

            if (barsToSend.Count > 0)
            {
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(barsToSend);
                WebSocketClientManager.BroadcastAsync(json).Wait();
                Console.WriteLine($"[bars-1m] Sent {barsToSend.Count} trade-based bars @ {nowParis:HH:mm:ss}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[bars-1m] ERROR: " + ex.Message);
        }
    }

    // ------------------------ BACKFILL (Paris-aligned) ------------------------
    private static async Task HandleBackfill(string arg, string mode, WebSocket socket)
{
    string symbol = (arg ?? "").Trim().Split(' ')[0];
    Console.WriteLine($"[backfill] request: {mode} for {symbol}");

    try
    {
        if (!Symbols.ContainsKey(symbol))
        {
            Console.WriteLine($"[backfill] unknown symbol {symbol}");
            return;
        }

        int symNo = Symbols[symbol];
        var parisTz = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        DateTime nowParis = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, parisTz);

        // =====================================================================
//  BFFULL — 30-day full backfill (oldest → newest, capped at 10k bars)
// =====================================================================
if (mode == "bffull" || mode == "bfall" || mode == "bfsym")
{
    DateTime startParis = nowParis.AddDays(-30);
    DateTime endParis = nowParis;
    if ((endParis - startParis).TotalMinutes < 10000)
        startParis = endParis.AddMinutes(-10000);

    DateTime startUtc = startParis;
    DateTime endUtc = endParis;

    Console.WriteLine($"[BFFULL] {symbol}: Paris window {startParis:yyyy-MM-dd HH:mm} → {endParis:yyyy-MM-dd HH:mm}");

    var allTicks = new List<TPRQuoteTick>();
    DateTime chunkStart = startUtc;
    int chunkCount = 0;

    while (chunkStart < endUtc)
    {
        DateTime chunkEnd = chunkStart.AddDays(5);
        if (chunkEnd > endUtc) chunkEnd = endUtc;

        TPRQuoteTickList trades, bids, asks;
        conn.GetIntradayChart(symNo, chunkStart, out trades, out bids, out asks, 0);

        if (trades != null && trades.Count > 0)
        {
            var valid = trades.Where(tt => tt.Time >= chunkStart && tt.Time <= chunkEnd).ToList();
            allTicks.AddRange(valid);
            Console.WriteLine($"[chunk] {chunkStart:u} → {chunkEnd:u}, ticks={valid.Count}");
        }

        chunkStart = chunkEnd;
        chunkCount++;
    }

    if (allTicks.Count == 0)
    {
        Console.WriteLine($"[backfill] no ticks for {symbol}");
        return;
    }

    allTicks = allTicks.OrderBy(t => t.Time).ToList();

    var firstTick = allTicks.First().Time;
    var lastTick = allTicks.Last().Time;

    Console.WriteLine($"[BFFULL-LAST] {symbol} {firstTick:yyyy-MM-dd HH:mm:ss} → {lastTick:yyyy-MM-dd HH:mm:ss}");

    // ✅ NEW LINE — record end time for bfauto continuation
    LastFullEndUtc[symbol] = lastTick;

    // --- Convert to dtohlcvi format ---
    var bars = allTicks.Select(t => new List<object>
    {
        t.Time.Year * 10000 + t.Time.Month * 100 + t.Time.Day,
        t.Time.Hour * 10000 + t.Time.Minute * 100 + t.Time.Second,
        t.Quote, t.Quote, t.Quote, t.Quote, (long)t.Volume, 0
    }).ToList();

    var histObj = new Dictionary<string, object>
    {
        { "hist", symbol },
        { "format", "dtohlcvi" },
        { "bars", bars }
    };

    string json = JsonConvert.SerializeObject(histObj);
    await WebSocketClientManager.SendSafeAsync(socket, new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)));

    Console.WriteLine($"[verify] Paris bars {bars.First()[0]} / {bars.First()[1]} → {bars.Last()[0]} / {bars.Last()[1]}");
    Console.WriteLine($"[backfill] sent {bars.Count} bars (oldest→newest) for {symbol}");
}

    // ============================================================================
//  BFAUTO — continuation when bffull ended early (10K> symbols etc.)
// ============================================================================
else if (mode == "bfauto")
{
    // reuse existing nowParis & parisTz declared earlier
    DateTime endUtc = nowParis;

    var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

    // --- ignore bfauto with no params (these are redundant) ---
    if (parts.Length < 3)
    {
        Console.WriteLine($"[BFAUTO] {symbol}: ignored bare bfauto (no params)");
        return;
    }

    Console.WriteLine($"[BFAUTO] {symbol}: raw arg='{arg}' now={nowParis:yyyy-MM-dd HH:mm:ss}");

    // --- parse timestamp from args ---
    DateTime startUtc;
    if (int.TryParse(parts[1], out int d1) && int.TryParse(parts[2], out int t1))
    {
        int y = d1 / 10000;
        int mo = (d1 / 100) % 100;
        int da = d1 % 100;
        int hh = t1 / 10000;
        int mm = (t1 / 100) % 100;
        int ss = t1 % 100;

        startUtc = new DateTime(y, mo, da, hh, mm, ss, DateTimeKind.Unspecified).AddSeconds(1);
        Console.WriteLine($"[BFAUTO] {symbol}: timestamp provided → {startUtc:yyyy-MM-dd HH:mm:ss}");
    }
    else
    {
        // fallback just to satisfy compiler — shouldn't ever happen
        startUtc = DateTime.UtcNow.AddDays(-1);
        Console.WriteLine($"[BFAUTO] {symbol}: malformed arg, defaulted 1 day back");
    }

    Console.WriteLine($"[BFAUTO] {symbol}: filling gap from {startUtc:yyyy-MM-dd HH:mm:ss} → {endUtc:yyyy-MM-dd HH:mm:ss}");

    // --- collect data in 5-day chunks until now ---
    var allTicks = new List<TPRQuoteTick>();
    DateTime chunkStart = startUtc;

    while (chunkStart < endUtc)
    {
        DateTime chunkEnd = chunkStart.AddDays(5);
        if (chunkEnd > endUtc)
            chunkEnd = endUtc;

        try
        {
            TPRQuoteTickList trades, bids, asks;
            conn.GetIntradayChart(symNo, chunkStart, out trades, out bids, out asks, 0);

            if (trades != null && trades.Count > 0)
            {
                var valid = trades.Where(tt => tt.Time >= chunkStart && tt.Time <= chunkEnd).ToList();
                allTicks.AddRange(valid);
                Console.WriteLine($"[chunk-bfauto] {chunkStart:u} → {chunkEnd:u}, ticks={valid.Count}");
            }
            else
            {
                Console.WriteLine($"[chunk-bfauto] {chunkStart:u} → {chunkEnd:u}, no ticks");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[chunk-bfauto] {chunkStart:u} → {chunkEnd:u}, error: {ex.Message}");
        }

        chunkStart = chunkEnd;
    }

    // --- convert ticks to bars and send ---
    if (allTicks.Count > 0)
    {
        var bars = allTicks.Select(t => new object[]
        {
            t.Time.Year * 10000 + t.Time.Month * 100 + t.Time.Day,
            t.Time.Hour * 10000 + t.Time.Minute * 100 + t.Time.Second,
            t.Quote, t.Quote, t.Quote, t.Quote, (long)t.Volume, 0
        }).ToList();

        var histObj = new Dictionary<string, object>
        {
            { "hist", symbol },
            { "format", "dtohlcvi" },
            { "bars", bars }
        };

        string json = JsonConvert.SerializeObject(histObj);
        await WebSocketClientManager.SendSafeAsync(socket,
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)));

        Console.WriteLine($"[backfill] sent {bars.Count} bars (bfauto continuation) for {symbol}");
    }
     else
    {
        Console.WriteLine($"[BFAUTO] {symbol}: no data returned for gap fill");
    }
}   // closes bfauto

}   // closes try
catch (Exception ex)
{
    Console.WriteLine("[backfill] ERROR: " + ex.Message);
}

}   // closes HandleBackfill() method
    // ------------------------ TRADE RECORDING (DEFINED) ------------------------
    private static void AddTrade(string sym, double price, int volume, DateTime parisLocalTime)
    {
        // parisLocalTime MUST be Paris-local (we ensure this at callsite)
        var list = tradeCache.GetOrAdd(sym, _ => new List<(DateTime, double, int)>());
        lock (list)
        {
            list.Add((parisLocalTime, price, volume));
        }

        if (DebugSymbols.Contains(sym) && DateTime.Now.Second % 10 == 0)
    Console.WriteLine($"[cache] {sym,-10} p={price:F4} v={volume,6} t={parisLocalTime:HH:mm:ss}");
    }

    // ========================================================================
    // Supporting classes
    // ========================================================================
    public class RTQRow
    {
        public int SymbolId { get; set; }
        public string Symbol { get; set; }
        public string FullName { get; set; }
        public string ISIN { get; set; }
        public string WKN { get; set; }
        public string Exchange { get; set; }
        public string Currency { get; set; }
        public double? Bid { get; set; }
        public double? Ask { get; set; }
        public double? Last { get; set; }
        public double? PrevClose { get; set; }
        public double? DayOpen { get; set; }
        public double? DayHigh { get; set; }
        public double? DayLow { get; set; }
        public long? DayVolume { get; set; }
        public double? Change { get; set; }
        public double? ChangePct { get; set; }
        public int BidSize { get; set; }
        public int AskSize { get; set; }
        public int LastTradeVolume { get; set; }

        public void RecalcChange()
        {
            if (PrevClose.HasValue && Last.HasValue && PrevClose.Value != 0)
            {
                Change = Last.Value - PrevClose.Value;
                ChangePct = (Change / PrevClose.Value) * 100.0;
            }
        }

        public object ToAmiJson()
        {
            var parisZone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, parisZone);

            if (LastTradeVolume > 0)
                DayVolume = (DayVolume ?? 0) + LastTradeVolume;

            var dict = new Dictionary<string, object>
            {
                ["n"] = Symbol,
                ["d"] = now.Year * 10000 + now.Month * 100 + now.Day,
                ["t"] = now.Hour * 10000 + now.Minute * 100 + now.Second,
                ["c"] = Last ?? 0.0,
                ["o"] = Last ?? 0.0,
                ["h"] = Last ?? 0.0,
                ["l"] = Last ?? 0.0,
                ["v"] = LastTradeVolume,
                ["s"] = DayVolume ?? 0,
                ["pc"] = PrevClose ?? 0.0,
                ["do"] = DayOpen ?? 0.0,
                ["dh"] = DayHigh ?? 0.0,
                ["dl"] = DayLow ?? 0.0,
                ["bp"] = Bid ?? 0.0,
                ["ap"] = Ask ?? 0.0,
                ["bs"] = BidSize,
                ["as"] = AskSize
            };

            LastTradeVolume = 0;
            return dict;
        }
    }

    public class FeedRouter
    {
        
		private readonly ConcurrentDictionary<int, RTQRow> _rows = new ConcurrentDictionary<int, RTQRow>();
        private readonly IReadOnlyDictionary<int, string> _idToTicker;
        public FeedRouter(IReadOnlyDictionary<int, string> idToTicker) { _idToTicker = idToTicker; }
        public IReadOnlyDictionary<int, RTQRow> AllRows => _rows;
        public void SetRow(RTQRow row) => _rows[row.SymbolId] = row;

        public void OnMessage(TPRPushFeedMessage msg)
        {
            if (msg is TPRQuoteTickMessage qt) HandleQuote(qt);
            else if (msg is TPRMarketDepthMessage md) HandleDepth(md);
        }

        private void HandleQuote(TPRQuoteTickMessage m)
{
    _idToTicker.TryGetValue(m.SymbolNo, out string sym);
    var row = _rows.GetOrAdd(m.SymbolNo, id => new RTQRow { SymbolId = id, Symbol = sym });

    if (m.QuoteType == TPRQuoteTypes.Last || m.QuoteType == TPRQuoteTypes.Bezahlt)
    {
        // Real executed trade
        row.Last = m.QuoteTick.Quote;
        row.LastTradeVolume = (int)m.QuoteTick.Volume;

        // ✅ Define Paris-local tick time from vendor tick
        var tickParis = m.QuoteTick.Time; // vendor already gives Paris-local

        // DEBUG (show vendor tick time as-is)
        if (DebugSymbols.Contains(row.Symbol))
            Console.WriteLine($"[TRADE] {row.Symbol} q={row.Last:F4} vol={row.LastTradeVolume} ticktime={tickParis:HH:mm:ss}");

        // Store tick time exactly as provided by vendor (already local for EU stocks)
        AddTrade(row.Symbol, row.Last.Value, row.LastTradeVolume, tickParis);
    }
    else if (m.QuoteType == TPRQuoteTypes.Bid)
    {
        row.Bid = m.QuoteTick.Quote;
        row.BidSize = (int)m.QuoteTick.Volume;
    }
    else if (m.QuoteType == TPRQuoteTypes.Ask)
    {
        row.Ask = m.QuoteTick.Quote;
        row.AskSize = (int)m.QuoteTick.Volume;
    }

    row.RecalcChange();
}

        private void HandleDepth(TPRMarketDepthMessage m)
        {
            if (_rows.TryGetValue(m.SymbolNo, out var row))
            {
                if (m.QuoteType == TPRQuoteTypes.Bid)
                {
                    row.Bid = m.QuoteTick.Quote;
                    row.BidSize = (int)m.QuoteTick.Volume;
                }
                else if (m.QuoteType == TPRQuoteTypes.Ask)
                {
                    row.Ask = m.QuoteTick.Quote;
                    row.AskSize = (int)m.QuoteTick.Volume;
                }
            }
        }
    }

    // ------------------------ WS CLIENT MANAGER ------------------------
    static class WebSocketClientManager
    {
        private static readonly List<WebSocket> clients = new List<WebSocket>();
        private static readonly object _lock = new object();
        private static readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

        public static void AddClient(WebSocket ws)
        {
            lock (_lock)
                clients.Add(ws);
            Console.WriteLine("Client connected.");
        }

        public static void Prune()
        {
            lock (_lock)
                clients.RemoveAll(ws => ws == null || ws.State != WebSocketState.Open);
        }

        public static async Task SendSafeAsync(WebSocket ws, ArraySegment<byte> seg)
        {
            await sendLock.WaitAsync();
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ws send error] " + ex.Message);
            }
            finally
            {
                sendLock.Release();
            }
        }

        public static async Task BroadcastAsync(string message)
        {
            Prune();
            byte[] buf = Encoding.UTF8.GetBytes(message);
            var seg = new ArraySegment<byte>(buf);
            var tasks = new List<Task>();

            lock (_lock)
            {
                foreach (var ws in clients.ToArray())
                    if (ws.State == WebSocketState.Open)
                        tasks.Add(SendSafeAsync(ws, seg));
            }

            try { await Task.WhenAll(tasks); }
            catch (Exception ex) { Console.WriteLine("[ws broadcast error] " + ex.Message); }
        }
    }
}
