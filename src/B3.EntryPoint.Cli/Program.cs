using System.Net;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

return args[0] switch
{
    "connect" => await ConnectAsync(args[1..]),
    _ => UnknownCommand(args[0]),
};

static async Task<int> ConnectAsync(string[] args)
{
    string? endpoint = null;
    string? sessionIdRaw = null;
    string? sessionVerIdRaw = null;
    string? firmRaw = null;
    string? accessKey = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--endpoint": endpoint = args[++i]; break;
            case "--session-id": sessionIdRaw = args[++i]; break;
            case "--session-ver-id": sessionVerIdRaw = args[++i]; break;
            case "--firm": firmRaw = args[++i]; break;
            case "--access-key": accessKey = args[++i]; break;
            default: Console.Error.WriteLine($"Unknown flag: {args[i]}"); return 2;
        }
    }

    if (endpoint is null || sessionIdRaw is null || sessionVerIdRaw is null || firmRaw is null || accessKey is null)
    {
        PrintUsage();
        return 2;
    }

    var ep = ParseEndpoint(endpoint);
    var options = new EntryPointClientOptions
    {
        Endpoint = ep,
        SessionId = uint.Parse(sessionIdRaw),
        SessionVerId = uint.Parse(sessionVerIdRaw),
        EnteringFirm = uint.Parse(firmRaw),
        Credentials = Credentials.FromUtf8(accessKey),
    };

    await using var client = new EntryPointClient(options);
    Console.WriteLine($"Connecting to {ep} ...");
    await client.ConnectAsync();
    Console.WriteLine($"Connected. State = {client.State}");
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

        Usage:
          b3-entrypoint-cli connect --endpoint HOST:PORT --session-id N --session-ver-id N --firm N --access-key KEY

        Bootstrap scope: connect performs TCP + Negotiate + Establish, prints the
        resulting state, and disconnects (Terminate). Order submission lands in a
        follow-up milestone.
        """);
}

static IPEndPoint ParseEndpoint(string s)
{
    var parts = s.Split(':');
    if (parts.Length != 2) throw new FormatException($"Expected HOST:PORT, got '{s}'.");
    var addresses = Dns.GetHostAddresses(parts[0]);
    return new IPEndPoint(addresses[0], int.Parse(parts[1]));
}
