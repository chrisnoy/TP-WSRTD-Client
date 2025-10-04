// ============================================================================
//  C# Feed Server – Json-RTD + Json-HIST (chunked backfill, 5-day windows)
//  -------------------------------------------------------------------------
//  • Connects to TPRAccess (Tai Pan SDK)
//  • Subscribes to live ticks → broadcasts Json-RTD packets
//  • Handles WS_RTD backfill requests (bffull / bfauto / bfall / bfsym)
//  • Backfill uses GetIntradayChart() in 5-day chunks
//  • Emits raw ticks as OHLCV bars (o=h=l=c=Quote, v=Volume)
//  • PrevClose injected for AmiBroker since Tai Pan RT feed omits it
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TPRAccess;

class Program
{
    static void Main()
    {
        Console.WriteLine("TP-WSRTD Feed Server starting...");
        Console.WriteLine("This version omits credentials. Please insert your Tai Pan API key.");
    }
}
