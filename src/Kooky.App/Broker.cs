using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kooky.Core;
using Kooky.Core.Services;
namespace Kooky.App;
internal sealed class Broker : IDisposable
{
    private readonly TerminalSessionManager terminals=new();
    private readonly StateStore state=new(AppPaths.Data);
    private readonly AgentCatalog agents=new(AppPaths.Data);
    private readonly WorkspaceService workspace=new();
    private readonly ConcurrentDictionary<Guid,Peer> peers=new();
    private readonly ConcurrentDictionary<string,SavedSession> sessions=new();
    private readonly CancellationTokenSource stop=new();
    private readonly object saveLock=new();
    private readonly object mutationLock=new();
    private bool preparingUpdate;
    private sealed record SavedSession(SessionInfo Info,SessionRequest Request);
    public static async Task RunAsync()
    {
        // Mutex ownership is thread-affine: retain it on this dedicated broker entry thread.
        using var mutex=new Mutex(true,AppPaths.Pipe+"-broker",out var owner);
        if(!owner) return;
        using var broker=new Broker();
        broker.Restore();
        broker.terminals.Output += chunk => broker.Broadcast("terminal.output",chunk);
        broker.terminals.Exited += info =>
        {
            if(broker.sessions.TryGetValue(info.Id,out var saved)) broker.sessions[info.Id]=saved with{Info=info};
            broker.Persist(); broker.Broadcast("terminal.exit",info); broker.Broadcast("sessions.changed",broker.List());
        };
        _=broker.DiscoveryLoop();
        try
        {
            while(!broker.stop.IsCancellationRequested)
            {
                NamedPipeServerStream? server=null;
                try
                {
                    server=new NamedPipeServerStream(AppPaths.Pipe,PipeDirection.InOut,NamedPipeServerStream.MaxAllowedServerInstances,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,65536,65536);
                    await server.WaitForConnectionAsync(broker.stop.Token);
                    _=broker.Accept(server);
                }
                catch(IOException ex){server?.Dispose();AppPaths.Log(ex);await Task.Delay(1000,broker.stop.Token);}
                catch{server?.Dispose();throw;}
            }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex){AppPaths.Log(ex);}
    }
    private SessionInfo[] List()
    {
        var live=terminals.List().ToDictionary(s=>s.Id);
        return sessions.Values.Select(s=>live.TryGetValue(s.Info.Id,out var info)?info:s.Info).OrderBy(s=>s.StartedAt).ToArray();
    }
    private async Task<object?> Dispatch(string type,JsonElement payload,Guid peerId)
    {
        string Str(string key)=>payload.GetProperty(key).GetString() ?? "";
        switch(type)
        {
            case "bootstrap": return new {state=state.Load("ui"),agents=agents.List(),sessions=List(),appVersion=AppInfo.Version,homePath=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),brokerPid=Environment.ProcessId,windowsBuild=Environment.OSVersion.Version.Build,warnings=state.RecoveryWarnings};
            case "state.save": state.Save("ui",payload);Broadcast("state.changed",new{state=payload},peerId);return null;
            case "session.list": return List();
            case "session.create": lock(mutationLock) return Create(payload.Deserialize<SessionRequest>(JsonDefaults.Options)!);
            case "session.resume":
              lock(mutationLock)
              {
                if(!sessions.TryGetValue(Str("sessionId"),out var saved)) throw new InvalidOperationException("Session no longer exists.");
                if(List().Any(s=>s.Id==saved.Info.Id && s.Running)) return List().First(s=>s.Id==saved.Info.Id);
                var resumed=Create(saved.Request with{NativeSessionId=saved.Info.NativeSessionId,Title=saved.Info.Title,Cwd=saved.Info.Cwd});
                sessions.TryRemove(saved.Info.Id,out _);
                if(terminals.List().Any(s=>s.Id==saved.Info.Id))terminals.Forget(saved.Info.Id);
                Persist();Broadcast("sessions.changed",List());return resumed;
              }
            case "session.input": terminals.Write(Str("sessionId"),Str("data"));return null;
            case "session.resize": terminals.Resize(Str("sessionId"),payload.GetProperty("cols").GetInt32(),payload.GetProperty("rows").GetInt32());return null;
            case "session.replay":
                var sid=Str("sessionId");
                if(!terminals.List().Any(s=>s.Id==sid)) return new TerminalReplay(sid,0,"",false);
                return terminals.Replay(sid);
            case "session.close":
              lock(mutationLock)
              {
                var closeId=Str("sessionId");
                sessions.TryRemove(closeId,out _);
                if(terminals.List().Any(s=>s.Id==closeId)) terminals.Forget(closeId);
                Persist();Broadcast("sessions.changed",List());return null;
              }
            case "session.move":
                var moveId=Str("sessionId");var workspaceId=Str("workspaceId");
                if(string.IsNullOrWhiteSpace(workspaceId))throw new ArgumentException("Workspace ID is required.");
                if(sessions.TryGetValue(moveId,out var moving)){moving.Info.WorkspaceId=workspaceId;sessions[moveId]=moving with{Request=moving.Request with{WorkspaceId=workspaceId}};}
                if(terminals.List().Any(s=>s.Id==moveId))terminals.Move(moveId,workspaceId);
                Persist();Broadcast("sessions.changed",List());return null;
            case "session.rename":
                var renameId=Str("sessionId");var title=Str("title").Trim();
                if(title.Length is <1 or >120) throw new ArgumentException("Title must contain 1–120 characters.");
                if(sessions.TryGetValue(renameId,out var old)) {old.Info.Title=title;sessions[renameId]=old with{Request=old.Request with{Title=title}};}
                if(terminals.List().Any(s=>s.Id==renameId)) terminals.Rename(renameId,title);
                Persist();Broadcast("sessions.changed",List());return null;
            case "agents.save": agents.Save(payload.GetProperty("agents").Deserialize<List<AgentProfile>>(JsonDefaults.Options)!);return agents.List();
            case "agents.history": return await Task.Run(()=>agents.History(Str("agentId"),Str("cwd")));
            case "workspace.inspect": return await workspace.InspectAsync(Str("path"));
            case "workspace.files": return await workspace.FilesAsync(Str("root"),Str("path"));
            case "performance": return await PerformanceSampler.SampleAsync(Environment.ProcessId,0,List());
            case "broker.prepareUpdate":
                lock(mutationLock){if(preparingUpdate)throw new InvalidOperationException("An update is already being prepared.");if(List().Any(s=>s.Running))throw new InvalidOperationException("A terminal is running. Stop all terminals before updating.");preparingUpdate=true;}return null;
            case "broker.cancelUpdate": lock(mutationLock)preparingUpdate=false;return null;
            case "broker.shutdown": lock(mutationLock)preparingUpdate=true;Broadcast("broker.stopping",new{});_=Task.Run(async()=>{await Task.Delay(350);stop.Cancel();});return null;
            default: throw new ArgumentException($"Unknown service command: {type}");
        }
    }
    private SessionInfo Create(SessionRequest request)
    {
        if(preparingUpdate)throw new InvalidOperationException("Kooky is preparing an update or shutting down. New terminals are temporarily disabled.");
        request=request with{TrackingId=Guid.NewGuid().ToString("N")};
        if(sessions.Values.Count(s=>s.Info.Running)>=64) throw new InvalidOperationException("Close a terminal before creating more (64 maximum).");
        var launch=agents.Resolve(request);
        var profile=agents.List().FirstOrDefault(a=>a.Id==request.AgentId);
        request=request with{Title=request.Title ?? profile?.Name ?? "Terminal"};
        var info=terminals.Create(request,launch);
        sessions[info.Id]=new(info,request with{NativeSessionId=launch.NativeSessionId});
        Persist();Broadcast("sessions.changed",List());
        return info;
    }
    private async Task DiscoveryLoop()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while(await timer.WaitForNextTickAsync(stop.Token))
            {
                var changed=false;
                foreach(var live in List())
                {
                    if(sessions.TryGetValue(live.Id,out var current) && (current.Info.Cwd!=live.Cwd || current.Info.Cols!=live.Cols || current.Info.Rows!=live.Rows))
                    {sessions[live.Id]=current with{Info=live};changed=true;}
                }
                foreach(var info in List().Where(s=>s.Running && s.NativeSessionId is null && s.AgentId!="shell"))
                {
                    try
                    {
                        var native=agents.DiscoverNativeSession(info);
                        if(native is null) continue;
                        if(terminals.List().Any(s=>s.Id==info.Id)) terminals.UpdateNativeSessionId(info.Id,native);
                        if(sessions.TryGetValue(info.Id,out var saved)){saved.Info.NativeSessionId=native;changed=true;}
                    }
                    catch(Exception ex){AppPaths.Log(ex);}
                }
                if(changed)Persist();
                Broadcast("sessions.changed",List());
            }
        }catch(OperationCanceledException){}
    }
    private void Persist()
    {
        lock(saveLock)
        {
            try
            {
                var live=List().ToDictionary(s=>s.Id);
                var data=sessions.Values.Select(s=>s with{Info=live.GetValueOrDefault(s.Info.Id,s.Info)}).ToArray();
                var bytes=JsonSerializer.SerializeToUtf8Bytes(data,JsonDefaults.Options);
                state.Save("sessions",new{encrypted=Convert.ToBase64String(ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser))});
            }
            catch(Exception ex){AppPaths.Log(ex);}
        }
    }
    private void Restore()
    {
        try
        {
            var saved=state.Load("sessions"); if(saved is null)return;
            var bytes=ProtectedData.Unprotect(Convert.FromBase64String(saved.Value.GetProperty("encrypted").GetString()!),null,DataProtectionScope.CurrentUser);
            foreach(var s in JsonSerializer.Deserialize<SavedSession[]>(bytes,JsonDefaults.Options)??[])
            {s.Info.Running=false;s.Info.ProcessId=0;sessions[s.Info.Id]=s;}
        }catch(Exception ex){AppPaths.Log(ex);}
    }
    private void Broadcast(string type,object payload,Guid? except=null)
    {
        var json=JsonSerializer.Serialize(new{type,payload},JsonDefaults.Options);
        foreach(var (id,peer) in peers) if(id!=except&&!peer.Queue.Writer.TryWrite(json)) peer.Stop.Cancel();
    }
    private sealed class Peer(NamedPipeServerStream pipe) : IDisposable
    {
        public readonly NamedPipeServerStream Pipe=pipe;
        public readonly Channel<string> Queue=Channel.CreateBounded<string>(new BoundedChannelOptions(512){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
        public readonly CancellationTokenSource Stop=new();
        public void Dispose(){Stop.Cancel();Queue.Writer.TryComplete();Pipe.Dispose();}
    }
    private async Task Accept(NamedPipeServerStream pipe)
    {
        var id=Guid.NewGuid();using var peer=new Peer(pipe);peers[id]=peer;
        var send=Task.Run(async()=>
        {
            try
            {
                using var writer=new StreamWriter(pipe,new UTF8Encoding(false),65536,true){AutoFlush=true};
                await foreach(var line in peer.Queue.Reader.ReadAllAsync(peer.Stop.Token)) await writer.WriteLineAsync(line.AsMemory(),peer.Stop.Token);
            }catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException){}finally{peer.Stop.Cancel();}
        });
        try
        {
            var running=new List<Task>();
            using var reader=new StreamReader(pipe,Encoding.UTF8,false,65536,true);
            while(!peer.Stop.IsCancellationRequested)
            {
                var line=await reader.ReadLineAsync(peer.Stop.Token);if(line is null)break;
                if(line.Length>8_000_000) throw new IOException("IPC request exceeds the limit.");
                running.RemoveAll(t=>t.IsCompleted);
                if(running.Count>=64)await Task.WhenAny(running);
                var request=line;
                // Preserve user input/mutation order; probes must not block keystrokes.
                using var envelope=JsonDocument.Parse(request);
                var kind=envelope.RootElement.GetProperty("type").GetString();
                if(kind is "session.input" or "session.resize" or "session.close" or "session.move" or "session.rename" or "state.save" or "agents.save")
                    await HandleRequest(peer,request,id);
                else running.Add(Task.Run(()=>HandleRequest(peer,request,id)));
            }
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
        catch(Exception ex){AppPaths.Log(ex);}
        finally {peers.TryRemove(id,out _);peer.Stop.Cancel();await send;}
    }
    private async Task HandleRequest(Peer peer,string line,Guid peerId)
    {
        string? rid=null;object response;
        try
        {
            using var doc=JsonDocument.Parse(line);var root=doc.RootElement;
            rid=root.GetProperty("id").GetString();var type=root.GetProperty("type").GetString()!;
            var result=await Dispatch(type,root.TryGetProperty("payload",out var p)?p:default,peerId);
            response=new{id=rid,ok=true,result};
        }
        catch(Exception ex){response=new{id=rid,ok=false,error=ex.Message};}
        try{await peer.Queue.Writer.WriteAsync(JsonSerializer.Serialize(response,JsonDefaults.Options),peer.Stop.Token);}catch(OperationCanceledException){}catch(System.Threading.Channels.ChannelClosedException){}
    }
    public void Dispose(){stop.Cancel();Persist();terminals.Dispose();foreach(var p in peers.Values)p.Dispose();}
}
