using System.Diagnostics;
using System.Text.Json;
using Kooky.Core;
namespace Kooky.App;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        AppPaths.Initialize(args);
        AppDomain.CurrentDomain.UnhandledException += (_,e) => AppPaths.Log(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));
        if (args.Contains("--broker")) { Broker.RunAsync().GetAwaiter().GetResult(); return; }
        if (args.Contains("--rpc")) { RunRpc(args).GetAwaiter().GetResult(); return; }
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_,e) => { AppPaths.Log(e.Exception); MessageBox.Show(e.Exception.Message,"Kooky",MessageBoxButtons.OK,MessageBoxIcon.Error); };
        using var hostMutex = new Mutex(true, AppPaths.Pipe + "-ui", out var isFirst);
        if (!isFirst && !args.Contains("--new-window"))
        {
            try { using var signal = EventWaitHandle.OpenExisting(AppPaths.Pipe+"-show"); signal.Set(); } catch { }
            return;
        }
        using var window = new MainWindow();
        Application.Run(window);
        if (isFirst) hostMutex.ReleaseMutex();
    }
    private static async Task RunRpc(string[] args)
    {
        var i=Array.IndexOf(args,"--rpc");
        var p=Array.IndexOf(args,"--payload");
        var output=Array.IndexOf(args,"--out");
        string result;
        try
        {
            using var client = new BrokerClient();
            await client.ConnectAsync();
            var data=p>=0 && p+1<args.Length ? JsonDocument.Parse(args[p+1]).RootElement.Clone() : JsonSerializer.SerializeToElement(new{});
            var value=await client.RequestAsync(args[i+1], data);
            result=JsonSerializer.Serialize(new {ok=true,result=value}, JsonDefaults.Options);
        }
        catch(Exception ex) { result=JsonSerializer.Serialize(new{ok=false,error=ex.Message}); Environment.ExitCode=1; }
        if(output>=0 && output+1<args.Length) await File.WriteAllTextAsync(args[output+1],result);
        else { NativeConsole.AttachConsole(uint.MaxValue); Console.WriteLine(result); }
    }
    public static Process StartSelf(params string[] args)
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden };
        foreach(var a in args) start.ArgumentList.Add(a);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(AppPaths.Data);
        return Process.Start(start)!;
    }
}
internal static partial class NativeConsole
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint processId);
}
