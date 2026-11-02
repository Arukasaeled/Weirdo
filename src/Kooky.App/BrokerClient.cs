using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Kooky.Core;
namespace Kooky.App;
internal sealed class BrokerClient : IDisposable
{
    private NamedPipeClientStream? pipe;
    private StreamWriter? writer;
    private readonly SemaphoreSlim writeLock = new(1,1);
    private readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource stopping = new();
    private int disposed;
    public event Action<JsonElement>? Event;
    public event Action? Disconnected;
    public async Task ConnectAsync()
    {
        try { await TryConnect(350); }
        catch (TimeoutException)
        {
            Program.StartSelf("--broker").Dispose();
            await TryConnect(15000);
        }
        writer = new StreamWriter(pipe!,new UTF8Encoding(false),65536,leaveOpen:true){AutoFlush=true};
        _=ReadLoop();
    }
    private async Task TryConnect(int timeout)
    {
        pipe?.Dispose();
        pipe = new NamedPipeClientStream(".",AppPaths.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout,stopping.Token);
    }
    public async Task<JsonElement> RequestAsync(string type, object? payload=null)
    {
        if(writer is null) throw new IOException("Terminal service is disconnected.");
        var id=Guid.NewGuid().ToString("N");
        var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id]=tcs;
        try
        {
            await writeLock.WaitAsync(stopping.Token);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(new{id,type,payload},JsonDefaults.Options)); }
            finally {writeLock.Release();}
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(40),stopping.Token);
        }
        finally {pending.TryRemove(id,out _);}
    }
    private async Task ReadLoop()
    {
        try
        {
            using var reader=new StreamReader(pipe!,Encoding.UTF8,false,65536,true);
            while(!stopping.IsCancellationRequested)
            {
                var line=await reader.ReadLineAsync(stopping.Token);
                if(line is null) break;
                using var doc=JsonDocument.Parse(line);
                var root=doc.RootElement;
                if(root.TryGetProperty("id",out var id) && id.ValueKind==JsonValueKind.String && pending.TryGetValue(id.GetString()!,out var tcs))
                {
                    if(root.GetProperty("ok").GetBoolean()) tcs.TrySetResult(root.GetProperty("result").Clone());
                    else tcs.TrySetException(new InvalidOperationException(root.GetProperty("error").GetString()));
                }
                else Event?.Invoke(root.Clone());
            }
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch(Exception ex) {AppPaths.Log(ex);}
        finally
        {
            foreach(var p in pending.Values) p.TrySetException(new IOException("Terminal service disconnected. Reopen Kooky to reconnect."));
            if(!stopping.IsCancellationRequested) Disconnected?.Invoke();
        }
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        stopping.Cancel();pipe?.Dispose();
        try{writer?.Dispose();}catch(IOException){}catch(ObjectDisposedException){}
    }
}
