using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Weirdo.BrokerHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            AppPaths.Initialize(args);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AppPaths.Log(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));

            if (args.Contains("--identity"))
            {
                Console.WriteLine(new JsonObject { ["pipe"] = AppPaths.Pipe, ["data"] = AppPaths.Data }.ToJsonString());
                return 0;
            }
            if (args.Contains("--version"))
            {
                Console.WriteLine(AppInfo.Version);
                return 0;
            }
            if (args.Contains("--broker"))
            {
                await Broker.RunAsync();
                return 0;
            }
            if (args.Contains("--rpc")) return await RunRpc(args);

            Console.Error.WriteLine("Weirdo.Broker --broker | --identity | --version | --rpc <type> [--payload <json>] [--out <file>] [--data-dir <directory>]");
            return 2;
        }
        catch (Exception error)
        {
            AppPaths.Log(error);
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task<int> RunRpc(string[] args)
    {
        JsonObject response;
        var exitCode = 0;
        try
        {
            var type = RequiredArgument(args, "--rpc");
            var payloadText = OptionalArgument(args, "--payload");
            var payload = payloadText is null ? new JsonObject() : JsonNode.Parse(payloadText);
            using var client = new BrokerClient();
            await client.ConnectAsync();
            var result = await client.RequestAsync(type, payload);
            response = new JsonObject { ["ok"] = true, ["result"] = result };
        }
        catch (Exception error)
        {
            response = new JsonObject { ["ok"] = false, ["error"] = error.Message };
            exitCode = 1;
        }

        var output = OptionalArgument(args, "--out");
        if (output is null) Console.WriteLine(response.ToJsonString());
        else await File.WriteAllTextAsync(output, response.ToJsonString());
        return exitCode;
    }

    private static string? OptionalArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value.");
        return args[index + 1];
    }

    private static string RequiredArgument(string[] args, string name) =>
        OptionalArgument(args, name) ?? throw new ArgumentException($"{name} requires a value.");

    public static Process StartSelf(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(AppPaths.Data);
        return Process.Start(start) ?? throw new IOException("Could not start the terminal service.");
    }
}
