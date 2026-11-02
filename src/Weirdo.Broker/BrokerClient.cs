using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Weirdo.BrokerHost;

/// <summary>Diagnostic client. The desktop client uses the same JSON-line protocol.</summary>
internal sealed class BrokerClient : IDisposable
{
    private NamedPipeClientStream? pipe;
    private StreamWriter? writer;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly CancellationTokenSource stopping = new();
    private int disposed;

    public async Task ConnectAsync()
    {
        try { await TryConnect(350); }
        catch (TimeoutException)
        {
            Program.StartSelf("--broker").Dispose();
            await TryConnect(15000);
        }
        writer = new StreamWriter(pipe!, new UTF8Encoding(false), 65536, leaveOpen: true) { AutoFlush = true };
        _ = ReadLoop();
    }

    private async Task TryConnect(int timeout)
    {
        pipe?.Dispose();
        pipe = new NamedPipeClientStream(".", AppPaths.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout, stopping.Token);
    }

    public async Task<JsonNode?> RequestAsync(string type, JsonNode? payload = null)
    {
        if (writer is null) throw new IOException("Terminal service is disconnected.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            var request = new JsonObject { ["id"] = id, ["type"] = type, ["payload"] = payload?.DeepClone() };
            await writeLock.WaitAsync(stopping.Token);
            try { await writer.WriteLineAsync(request.ToJsonString().AsMemory(), stopping.Token); }
            finally { writeLock.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(40), stopping.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task ReadLoop()
    {
        try
        {
            using var reader = new StreamReader(pipe!, Encoding.UTF8, false, 65536, true);
            while (!stopping.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(stopping.Token);
                if (line is null) break;
                var response = JsonNode.Parse(line);
                var id = response?["id"]?.GetValue<string>();
                if (id is null || !pending.TryGetValue(id, out var completion)) continue;
                if (response!["ok"]?.GetValue<bool>() == true) completion.TrySetResult(response["result"]?.DeepClone());
                else completion.TrySetException(new InvalidOperationException(response["error"]?.GetValue<string>()));
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { AppPaths.Log(error); }
        finally
        {
            foreach (var completion in pending.Values)
                completion.TrySetException(new IOException("Terminal service disconnected."));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        pipe?.Dispose();
        try { writer?.Dispose(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
