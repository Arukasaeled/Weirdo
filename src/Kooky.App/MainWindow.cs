using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kooky.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace Kooky.App;
internal sealed class MainWindow : Form
{
    private readonly WebView2 web=new(){Dock=DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(33,37,43)};
    private BrokerClient? client;
    private readonly NotifyIcon? tray;
    private readonly bool monitor;
    private readonly bool secondary;
    private readonly List<MainWindow> secondaryWindows=[];
    private MainWindow? monitorWindow;
    private bool exiting,ready,reconnecting;
    private readonly TaskCompletionSource connection=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly EventWaitHandle? showSignal;
    private readonly RegisteredWaitHandle? showWait;
    private int brokerPid;
    private readonly System.Windows.Forms.Timer updateTimer=new(){Interval=30000};
    private string? notifiedVersion;
    private bool startupRecorded;
    private bool resourcesDisposed;
    public MainWindow(bool monitor=false,bool secondary=false)
    {
        this.monitor=monitor;
        this.secondary=secondary;
        Text=monitor?"Kooky · Agent Monitor":"Kooky";
        Size=monitor?new Size(580,640):new Size(1320,880);MinimumSize=monitor?new Size(420,350):new Size(780,480);
        StartPosition=FormStartPosition.CenterScreen;BackColor=Color.FromArgb(33,37,43);
        Icon=MakeIcon();Controls.Add(web);
        if(!monitor&&!secondary)
        {
            tray=new NotifyIcon{Icon=Icon,Text="Kooky · 终端工作台",Visible=true};
            var menu=new ContextMenuStrip();menu.Items.Add("Open Kooky",null,(_,_)=>ShowMain());
            menu.Items.Add("Agent monitor",null,(_,_)=>ShowMonitor());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Close interface, keep terminals",null,(_,_)=>{exiting=true;Close();});
            menu.Items.Add("Quit and stop all terminals",null,async(_,_)=>await Quit());
            tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>ShowMain();
            if(AppPaths.Debug)tray.BalloonTipShown+=(_,_)=>RecordDesktopEvent("notification-shown");
            showSignal=new EventWaitHandle(false,EventResetMode.AutoReset,AppPaths.Pipe+"-show");
            showWait=ThreadPool.RegisterWaitForSingleObject(showSignal,(_,_)=>Ui(ShowMain),null,-1,false);
        }
        Shown+=async(_,_)=>await Initialize();
        updateTimer.Tick+=async(_,_)=>{updateTimer.Interval=6*60*60*1000;await CheckUpdates();};
        FormClosing+=(_,e)=>{if(!exiting&&!monitor&&!secondary&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);if(monitor||secondary)return;
        // Ctrl+Alt+K and Ctrl+Alt+Shift+K; failure is reported instead of overriding another app.
        var first=RegisterHotKey(Handle,1,0x4000|0x2|0x1,(uint)Keys.K);
        var second=RegisterHotKey(Handle,2,0x4000|0x2|0x1|0x4,(uint)Keys.K);
        if(!first||!second)AppPaths.Log(new InvalidOperationException("A Kooky global shortcut is already registered by another application."));
    }
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x0312){RecordDesktopEvent("hotkey-"+m.WParam);if(m.WParam==1){if(Visible&&ContainsFocus)Hide();else ShowMain();}else ShowMonitor();}
        base.WndProc(ref m);
    }
    private async Task Initialize()
    {
        try
        {
            await Connect();connection.TrySetResult();
            var flags=AppPaths.Debug?"--remote-debugging-port=9334":"";
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(AppPaths.Data,"WebView2"),new CoreWebView2EnvironmentOptions(flags));
            await web.EnsureCoreWebView2Async(env);
            var core=web.CoreWebView2;core.Settings.AreDevToolsEnabled=AppPaths.Debug;
            core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.IsStatusBarEnabled=false;
            core.Settings.AreBrowserAcceleratorKeysEnabled=false;
            core.SetVirtualHostNameToFolderMapping("kooky.local",Path.Combine(AppContext.BaseDirectory,"web"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived+=OnWebMessage;
            core.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith("https://kooky.local/",StringComparison.OrdinalIgnoreCase))e.Cancel=true;};
            core.NewWindowRequested+=(_,e)=>{e.Handled=true;if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http")Process.Start(new ProcessStartInfo(e.Uri){UseShellExecute=true});};
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.ProcessFailed+=(_,e)=>{AppPaths.Log(new Exception($"WebView2 process failed: {e.ProcessFailedKind}"));if(e.ProcessFailedKind==CoreWebView2ProcessFailedKind.RenderProcessExited)core.Reload();};
            core.NavigationCompleted+=(_,e)=>{ready=e.IsSuccess;if(!e.IsSuccess)MessageBox.Show("The local interface could not load: "+e.WebErrorStatus,"Kooky");};
            core.Navigate("https://kooky.local/index.html"+(monitor?"?monitor=1":""));
            if(!monitor&&!secondary)updateTimer.Start();
        }
        catch(Exception ex){connection.TrySetException(ex);AppPaths.Log(ex);MessageBox.Show("Kooky could not start.\n\n"+ex.Message+"\n\nInstall Microsoft Edge WebView2 Runtime if it is missing.","Kooky",MessageBoxButtons.OK,MessageBoxIcon.Error);exiting=true;Close();}
    }
    private async Task Connect()
    {
        var next=new BrokerClient();next.Event+=OnBrokerEvent;next.Disconnected+=()=>Ui(()=>_=Reconnect());
        await next.ConnectAsync();client?.Dispose();client=next;
        var boot=await next.RequestAsync("bootstrap");brokerPid=boot.GetProperty("brokerPid").GetInt32();
    }
    private async Task Reconnect()
    {
        if(reconnecting||exiting||IsDisposed)return;reconnecting=true;
        Post(new{type="connection",payload=new{connected=false}});
        for(var attempt=0;attempt<5&&!exiting;attempt++)
        {
            try{await Connect();reconnecting=false;if(ready)web.CoreWebView2.Reload();return;}
            catch(Exception ex){AppPaths.Log(ex);await Task.Delay(1500);}
        }
        reconnecting=false;
        MessageBox.Show("The terminal service is unavailable. Reopen Kooky to retry; saved sessions remain in local app data.","Kooky");
    }
    private void OnBrokerEvent(JsonElement e)
    {
        Ui(()=>
        {
            if(e.TryGetProperty("type",out var stoppingType)&&stoppingType.GetString()=="broker.stopping") {exiting=true;monitorWindow?.Close();Close();return;}
            if(ready)web.CoreWebView2.PostWebMessageAsJson(e.GetRawText());
            if(!monitor&&!secondary&&e.TryGetProperty("type",out var t)&&t.GetString()=="terminal.exit")
            {
                var info=e.GetProperty("payload").Deserialize<SessionInfo>(JsonDefaults.Options);
                if(info is not null&&!ContainsFocus)tray?.ShowBalloonTip(4000,"Kooky · "+info.Title,$"Process exited with code {info.ExitCode?.ToString()??"unknown"}.",info.ExitCode==0?ToolTipIcon.Info:ToolTipIcon.Warning);
            }
        });
    }
    private async void OnWebMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if(!e.Source.StartsWith("https://kooky.local/",StringComparison.OrdinalIgnoreCase))return;
        string? id=null;
        try
        {
            using var doc=JsonDocument.Parse(e.WebMessageAsJson);var root=doc.RootElement;
            id=root.GetProperty("id").GetString();var type=root.GetProperty("type").GetString()!;
            var payload=root.TryGetProperty("payload",out var p)?p.Clone():JsonSerializer.SerializeToElement(new{});
            await connection.Task;
            object? result;
            switch(type)
            {
                case "dialog.folder":using(var picker=new FolderBrowserDialog{Description="Open a project folder",UseDescriptionForTitle=true})result=picker.ShowDialog(this)==DialogResult.OK?picker.SelectedPath:null;break;
                case "clipboard.read":result=Clipboard.ContainsText()?Clipboard.GetText():"";break;
                case "clipboard.write":Clipboard.SetText(payload.GetProperty("text").GetString()??"");result=null;break;
                case "window.hide":Hide();result=null;break;
                case "window.show":if(monitor){using var signal=EventWaitHandle.OpenExisting(AppPaths.Pipe+"-show");signal.Set();}else ShowMain();result=null;break;
                case "window.monitor":WorkbenchOwner().ShowMonitor();result=null;break;
                case "window.new":WorkbenchOwner().NewWindow();result=null;break;
                case "window.minimize":WindowState=FormWindowState.Minimized;result=null;break;
                case "window.maximize":WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;result=null;break;
                case "app.ready":
                    if(!monitor&&!secondary&&!startupRecorded)
                    {
                        startupRecorded=true;using var process=Process.GetCurrentProcess();
                        var report=new{processId=Environment.ProcessId,at=DateTimeOffset.UtcNow,elapsedMs=(DateTime.UtcNow-process.StartTime.ToUniversalTime()).TotalMilliseconds,version=AppInfo.Version,debug=AppPaths.Debug,executable=Environment.ProcessPath};
                        await File.WriteAllTextAsync(Path.Combine(AppPaths.Data,"startup.json"),JsonSerializer.Serialize(report,JsonDefaults.Options));
                        var updateError=Path.Combine(AppPaths.Data,"updates","update-error.txt");
                        if(File.Exists(updateError))
                        {
                            var message=await File.ReadAllTextAsync(updateError);
                            File.Move(updateError,updateError+".reported-"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            Post(new{type="host.error",payload=new{message="上次更新未完成："+message[..Math.Min(message.Length,500)]}});
                        }
                    }
                    result=null;break;
                case "path.open":
                    var path=payload.GetProperty("path").GetString()!;
                    if(!Path.IsPathFullyQualified(path)||(!File.Exists(path)&&!Directory.Exists(path)))throw new ArgumentException("Path does not exist.");
                    Process.Start(new ProcessStartInfo(path){UseShellExecute=true});result=null;break;
                case "performance":var sessions=(await client!.RequestAsync("session.list")).Deserialize<SessionInfo[]>(JsonDefaults.Options)??[];result=await PerformanceSampler.SampleAsync(brokerPid,Environment.ProcessId,sessions);break;
                case "updates.configure":result=UpdateService.Configure(payload.GetProperty("feedUrl").GetString()!);_=CheckUpdates();break;
                case "updates.check":result=await UpdateService.CheckAsync();break;
                case "updates.install":
                    var active=(await client!.RequestAsync("session.list")).Deserialize<SessionInfo[]>(JsonDefaults.Options)??[];
                    if(active.Any(s=>s.Running))throw new InvalidOperationException("Stop running terminals before installing an update. Workspaces and native session IDs will be retained.");
                    result=await UpdateService.InstallAsync(this,brokerPid,async()=>{await client.RequestAsync("broker.prepareUpdate");},async()=>{await client.RequestAsync("broker.cancelUpdate");},async()=>{await client.RequestAsync("broker.shutdown");exiting=true;Close();});break;
                default:result=await client!.RequestAsync(type,payload);break;
            }
            Post(new{id,ok=true,result});
        }
        catch(Exception ex){Post(new{id,ok=false,error=ex.Message});}
    }
    private void Post(object value){if(!IsDisposed&&web.CoreWebView2 is not null)web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value,JsonDefaults.Options));}
    private static void RecordDesktopEvent(string kind)
    {
        if(!AppPaths.Debug)return;
        try{File.AppendAllText(Path.Combine(AppPaths.Data,"desktop-events.jsonl"),JsonSerializer.Serialize(new{kind,at=DateTimeOffset.UtcNow,processId=Environment.ProcessId})+"\n");}catch(IOException){}
    }
    private async Task CheckUpdates()
    {
        if(monitor||exiting)return;
        try
        {
            var result=JsonSerializer.SerializeToElement(await UpdateService.CheckAsync(),JsonDefaults.Options);
            if(result.GetProperty("available").GetBoolean()&&result.TryGetProperty("version",out var version)&&version.GetString()!=notifiedVersion)
            {
                notifiedVersion=version.GetString();
                tray?.ShowBalloonTip(6000,"Kooky update available",$"Version {notifiedVersion} is available. Open Settings → Updates to install.",ToolTipIcon.Info);
                Post(new{type="updates.available",payload=new{version=notifiedVersion}});
            }
        }
        catch(Exception ex){AppPaths.Log(new IOException("Update check failed: "+ex.Message));}
    }
    private void Ui(Action action){if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke(()=>{if(!IsDisposed)action();});}catch(InvalidOperationException){}}
    public void ShowMain(){Show();if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;Activate();web.Focus();}
    private MainWindow WorkbenchOwner()=>Application.OpenForms.OfType<MainWindow>().FirstOrDefault(w=>!w.monitor&&!w.secondary)??this;
    private void NewWindow(){var next=new MainWindow(secondary:true);secondaryWindows.Add(next);next.FormClosed+=(_,_)=>secondaryWindows.Remove(next);next.ShowMain();}
    private void ShowMonitor(){if(monitor){ShowMain();return;}if(monitorWindow is null||monitorWindow.IsDisposed)monitorWindow=new MainWindow(true);monitorWindow.ShowMain();}
    private async Task Quit()
    {
        if(MessageBox.Show("Stop all terminals and quit Kooky? Native Agent conversations can be resumed later.","Quit Kooky",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        try{await client!.RequestAsync("broker.shutdown");}catch(Exception ex){AppPaths.Log(ex);}
        exiting=true;monitorWindow?.Close();Close();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing&&!resourcesDisposed)
        {
            resourcesDisposed=true;exiting=true;
            if(IsHandleCreated){UnregisterHotKey(Handle,1);UnregisterHotKey(Handle,2);}
            updateTimer.Dispose();showWait?.Unregister(null);showSignal?.Dispose();client?.Dispose();tray?.Dispose();
            if(monitorWindow is {IsDisposed:false})monitorWindow.Dispose();
            foreach(var child in secondaryWindows.ToArray())if(!child.IsDisposed)child.Dispose();
        }
        base.Dispose(disposing);
    }
    private static Icon MakeIcon()
    {
        using var b=new Bitmap(32,32);using(var g=Graphics.FromImage(b)){g.Clear(Color.FromArgb(38,43,51));using var pen=new Pen(Color.FromArgb(225,151,104),3);g.DrawLines(pen,[new Point(8,9),new Point(15,16),new Point(8,23)]);g.DrawLine(pen,17,23,25,23);}
        var handle=b.GetHicon();try{return (Icon)Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}
    }
    [DllImport("user32.dll")]private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr handle);
}
