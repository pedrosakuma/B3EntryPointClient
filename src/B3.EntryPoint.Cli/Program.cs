using System.Net;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.DropCopy;
using B3.EntryPoint.Client.Models;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

return args[0] switch
{
    "connect"  => await ConnectAsync(args[1..]),
    "submit"   => await SubmitAsync(args[1..]),
    "replace"  => await ReplaceAsync(args[1..]),
    "cancel"   => await CancelAsync(args[1..]),
    "dropcopy" => await DropCopyAsync(args[1..]),
    _ => UnknownCommand(args[0]),
};

static EntryPointClientOptions BuildOptions(Dictionary<string, string> flags, SessionProfile profile = SessionProfile.OrderEntry)
{
    var ep = ParseEndpoint(flags["--endpoint"]);
    return new EntryPointClientOptions
    {
        Endpoint = ep,
        SessionId = uint.Parse(flags["--session-id"]),
        SessionVerId = uint.Parse(flags["--session-ver-id"]),
        EnteringFirm = uint.Parse(flags["--firm"]),
        Credentials = Credentials.FromUtf8(flags["--access-key"]),
        Profile = profile,
    };
}

static Dictionary<string, string> ParseFlags(string[] args, params string[] required)
{
    var map = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--"))
            throw new ArgumentException($"Unknown positional arg: {args[i]}");
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {args[i]}");
        map[args[i]] = args[++i];
    }
    foreach (var r in required)
        if (!map.ContainsKey(r))
            throw new ArgumentException($"Missing required flag: {r}");
    return map;
}

static async Task<int> ConnectAsync(string[] args)
{
    var flags = ParseFlags(args, "--endpoint", "--session-id", "--session-ver-id", "--firm", "--access-key");
    var options = BuildOptions(flags);
    await using var client = new EntryPointClient(options);
    Console.WriteLine($"Connecting to {options.Endpoint} ...");
    await client.ConnectAsync();
    Console.WriteLine($"Connected. State = {client.State}");
    return 0;
}

static async Task<int> SubmitAsync(string[] args)
{
    var flags = ParseFlags(args, "--endpoint", "--session-id", "--session-ver-id", "--firm", "--access-key",
        "--clordid", "--security-id", "--side", "--ord-type", "--qty");
    var options = BuildOptions(flags);
    await using var client = new EntryPointClient(options);
    await client.ConnectAsync();
    var req = new NewOrderRequest
    {
        ClOrdID = ClOrdID.Parse(flags["--clordid"]),
        SecurityId = ulong.Parse(flags["--security-id"]),
        Side = Enum.Parse<Side>(flags["--side"], ignoreCase: true),
        OrderType = Enum.Parse<OrderType>(flags["--ord-type"], ignoreCase: true),
        OrderQty = ulong.Parse(flags["--qty"]),
        Price = flags.TryGetValue("--price", out var p) ? decimal.Parse(p) : null,
    };
    var id = await client.SubmitAsync(req);
    Console.WriteLine($"Submitted ClOrdID={id}");
    return 0;
}

static async Task<int> ReplaceAsync(string[] args)
{
    var flags = ParseFlags(args, "--endpoint", "--session-id", "--session-ver-id", "--firm", "--access-key",
        "--clordid", "--orig-clordid", "--security-id", "--side", "--ord-type", "--qty");
    var options = BuildOptions(flags);
    await using var client = new EntryPointClient(options);
    await client.ConnectAsync();
    var req = new ReplaceOrderRequest
    {
        ClOrdID = ClOrdID.Parse(flags["--clordid"]),
        OrigClOrdID = ClOrdID.Parse(flags["--orig-clordid"]),
        SecurityId = ulong.Parse(flags["--security-id"]),
        Side = Enum.Parse<Side>(flags["--side"], ignoreCase: true),
        OrderType = Enum.Parse<OrderType>(flags["--ord-type"], ignoreCase: true),
        OrderQty = ulong.Parse(flags["--qty"]),
        Price = flags.TryGetValue("--price", out var p) ? decimal.Parse(p) : null,
    };
    var id = await client.ReplaceAsync(req);
    Console.WriteLine($"Replaced ClOrdID={id}");
    return 0;
}

static async Task<int> CancelAsync(string[] args)
{
    var flags = ParseFlags(args, "--endpoint", "--session-id", "--session-ver-id", "--firm", "--access-key",
        "--clordid", "--orig-clordid", "--security-id", "--side");
    var options = BuildOptions(flags);
    await using var client = new EntryPointClient(options);
    await client.ConnectAsync();
    var req = new CancelOrderRequest
    {
        ClOrdID = ClOrdID.Parse(flags["--clordid"]),
        OrigClOrdID = ClOrdID.Parse(flags["--orig-clordid"]),
        SecurityId = ulong.Parse(flags["--security-id"]),
        Side = Enum.Parse<Side>(flags["--side"], ignoreCase: true),
    };
    await client.CancelAsync(req);
    Console.WriteLine("Cancel sent");
    return 0;
}

static async Task<int> DropCopyAsync(string[] args)
{
    if (args.Length == 0 || args[0] != "tail")
    {
        Console.Error.WriteLine("Usage: dropcopy tail --endpoint ... --session-id ... --session-ver-id ... --firm ... --access-key ...");
        return 2;
    }
    var flags = ParseFlags(args[1..], "--endpoint", "--session-id", "--session-ver-id", "--firm", "--access-key");
    var options = BuildOptions(flags, SessionProfile.DropCopy);
    await using var client = new DropCopyClient(options);
    Console.WriteLine($"Connecting drop-copy to {options.Endpoint} ...");
    await client.ConnectAsync();
    Console.WriteLine("Tailing events. Ctrl+C to stop.");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try
    {
        await foreach (var evt in client.Events(cts.Token))
            Console.WriteLine(evt);
    }
    catch (OperationCanceledException) { }
    return 0;
}

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("""
        b3-entrypoint-cli — manual smoke tool for B3.EntryPoint.Client.

        Common flags (all commands except `help`):
          --endpoint HOST:PORT --session-id N --session-ver-id N --firm N --access-key KEY

        Commands:
          connect
              TCP + Negotiate + Establish, prints state, then disconnects (Terminate).

          submit  --clordid X --security-id N --side BUY|SELL --ord-type Limit|Market|... --qty N [--price D]
              Submit a NewOrderSingle.

          replace --clordid X --orig-clordid Y --security-id N --side BUY|SELL --ord-type ... --qty N [--price D]
              Submit an OrderCancelReplaceRequest.

          cancel  --clordid X --orig-clordid Y --security-id N --side BUY|SELL
              Submit an OrderCancelRequest.

          dropcopy tail
              Open a DropCopy session and stream EntryPointEvents to stdout until Ctrl+C.
        """);
}

static IPEndPoint ParseEndpoint(string s)
{
    var parts = s.Split(':');
    if (parts.Length != 2) throw new FormatException($"Expected HOST:PORT, got '{s}'.");
    var addresses = Dns.GetHostAddresses(parts[0]);
    return new IPEndPoint(addresses[0], int.Parse(parts[1]));
}
