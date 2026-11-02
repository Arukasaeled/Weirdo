using System.Diagnostics;
using System.Runtime.InteropServices;
using Kooky.Core;
namespace Kooky.App;
internal static class PerformanceSampler
{
    private sealed record Sample(int Id,string Name,long WorkingSet,long Private,double CpuMs);
    private static Dictionary<int,Sample> Capture(IEnumerable<int> ids)
    {
        var result=new Dictionary<int,Sample>();
        foreach(var id in ids.Distinct())try{using var p=Process.GetProcessById(id);result[id]=new(id,p.ProcessName,p.WorkingSet64,p.PrivateMemorySize64,p.TotalProcessorTime.TotalMilliseconds);}catch{}
        return result;
    }
    public static async Task<object> SampleAsync(int brokerPid,int uiPid,IEnumerable<SessionInfo> sessions)
    {
        var parents=ParentMap();
        HashSet<int> Descendants(int root)
        {
            var found=new HashSet<int>();if(root<=0)return found;found.Add(root);
            bool more;do{more=false;foreach(var p in parents)if(found.Contains(p.Value)&&found.Add(p.Key))more=true;}while(more);
            return found;
        }
        var brokerTree=Descendants(brokerPid);var uiTree=Descendants(uiPid);
        var agentPids=new HashSet<int>();var shellPids=new HashSet<int>();
        foreach(var session in sessions.Where(s=>s.Running))(session.AgentId=="shell"?shellPids:agentPids).UnionWith(Descendants(session.ProcessId));
        var all=brokerTree.Union(uiTree).Union(agentPids).Union(shellPids).ToArray();
        var first=Capture(all);var sw=Stopwatch.StartNew();await Task.Delay(1000);var second=Capture(all);sw.Stop();
        var rows=second.Values.Select(p=>new {
            pid=p.Id,name=p.Name,
            group=p.Name.Equals("conhost",StringComparison.OrdinalIgnoreCase)||p.Name.Equals("OpenConsole",StringComparison.OrdinalIgnoreCase)?"terminal":agentPids.Contains(p.Id)?"agents":shellPids.Contains(p.Id)?"shells":p.Name.StartsWith("msedgewebview2",StringComparison.OrdinalIgnoreCase)?"rendering":"host",
            workingSetBytes=p.WorkingSet,privateBytes=p.Private,
            cpuPercent=first.TryGetValue(p.Id,out var f)?Math.Max(0,p.CpuMs-f.CpuMs)/sw.Elapsed.TotalMilliseconds/Environment.ProcessorCount*100:0
        }).ToArray();
        return new{at=DateTimeOffset.UtcNow,intervalMs=sw.Elapsed.TotalMilliseconds,logicalProcessors=Environment.ProcessorCount,cpuBasis="percent of total machine capacity",groups=rows.GroupBy(r=>r.group).Select(g=>new{name=g.Key,pids=g.Select(p=>p.pid),workingSetBytes=g.Sum(p=>p.workingSetBytes),privateBytes=g.Sum(p=>p.privateBytes),cpuPercent=g.Sum(p=>p.cpuPercent)}),processes=rows};
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct PROCESSENTRY32 {public uint size,usage,pid;public UIntPtr heap;public uint module,threads,parent;public int priority;public uint flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string exe;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32FirstW(IntPtr snapshot,ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32NextW(IntPtr snapshot,ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    private static Dictionary<int,int> ParentMap()
    {
        var result=new Dictionary<int,int>();var h=CreateToolhelp32Snapshot(2,0);if(h==new IntPtr(-1))return result;
        try{var e=new PROCESSENTRY32{size=(uint)Marshal.SizeOf<PROCESSENTRY32>(),exe=""};if(Process32FirstW(h,ref e))do{result[(int)e.pid]=(int)e.parent;}while(Process32NextW(h,ref e));}finally{CloseHandle(h);}return result;
    }
}
