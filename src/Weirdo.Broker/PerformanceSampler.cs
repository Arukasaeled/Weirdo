using System.Diagnostics;
using System.Runtime.InteropServices;
using Weirdo.Core;

namespace Weirdo.BrokerHost;

internal static class PerformanceSampler
{
    private sealed record Sample(int Id, string Name, long WorkingSet, long Private, double CpuMs);

    private static Dictionary<int, Sample> Capture(IEnumerable<int> ids)
    {
        var result = new Dictionary<int, Sample>();
        foreach (var id in ids.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(id);
                result[id] = new(id, process.ProcessName, process.WorkingSet64, process.PrivateMemorySize64, process.TotalProcessorTime.TotalMilliseconds);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return result;
    }

    public static async Task<ResourceMeasurements> SampleAsync(int brokerPid, int hostPid, IEnumerable<SessionInfo> sessions)
    {
        var parents = ParentMap();
        HashSet<int> Descendants(int root)
        {
            var found = new HashSet<int>();
            if (root <= 0) return found;
            found.Add(root);
            bool more;
            do
            {
                more = false;
                foreach (var process in parents)
                    if (found.Contains(process.Value) && found.Add(process.Key)) more = true;
            } while (more);
            return found;
        }

        var brokerTree = Descendants(brokerPid);
        var hostTree = Descendants(hostPid);
        var agentPids = new HashSet<int>();
        var shellPids = new HashSet<int>();
        foreach (var session in sessions.Where(session => session.Running))
            (session.AgentId == "shell" ? shellPids : agentPids).UnionWith(Descendants(session.ProcessId));

        var all = brokerTree.Union(hostTree).Union(agentPids).Union(shellPids).ToArray();
        var first = Capture(all);
        var timer = Stopwatch.StartNew();
        await Task.Delay(1000);
        var second = Capture(all);
        timer.Stop();

        string Group(Sample process)
        {
            if (process.Name.Equals("conhost", StringComparison.OrdinalIgnoreCase) || process.Name.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase)) return "terminal";
            if (agentPids.Contains(process.Id)) return "agents";
            if (shellPids.Contains(process.Id)) return "shells";
            if (process.Name.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase)) return "rendering";
            return "host";
        }

        var rows = second.Values.Select(process => new ProcessMeasurement(
            process.Id, process.Name, Group(process), process.WorkingSet, process.Private,
            first.TryGetValue(process.Id, out var before)
                ? Math.Max(0, process.CpuMs - before.CpuMs) / timer.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100 : 0)).ToArray();
        var groups = rows.GroupBy(process => process.Group).Select(group => new ResourceGroup(
            group.Key, group.Select(process => process.Pid).ToArray(), group.Sum(process => process.WorkingSetBytes),
            group.Sum(process => process.PrivateBytes), group.Sum(process => process.CpuPercent))).ToArray();
        return new(DateTimeOffset.UtcNow, timer.Elapsed.TotalMilliseconds, Environment.ProcessorCount,
            "percent of total machine capacity", groups, rows);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static Dictionary<int, int> ParentMap()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
            if (Process32FirstW(snapshot, ref entry))
                do { result[(int)entry.Pid] = (int)entry.Parent; }
                while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }
}
