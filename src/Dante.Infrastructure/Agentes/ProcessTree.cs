using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
// Descendant discovery that .NET does not expose: once the root exits, its children are no longer reachable through
// Process.Kill(entireProcessTree), so InteractiveAgentProcess records them while the root is alive. Each process is
// identified by PID and start time, so a reused PID is never mistaken for a tracked process.
internal static class ProcessTree
{
    public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    public static DateTime? TryGetStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public static List<(int Id, DateTime StartTime)> GetDescendants(int rootId, DateTime rootStartTime)
    {
        var descendants = new List<(int Id, DateTime StartTime)>();
        if (!IsSupported)
        {
            return descendants;
        }

        var childrenByParent = SnapshotParents()
            .GroupBy(entry => entry.ParentId)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Id).ToList());
        var pending = new Queue<(int Id, DateTime StartTime)>([(rootId, rootStartTime)]);
        var visited = new HashSet<int> { rootId };
        while (pending.TryDequeue(out var parent))
        {
            foreach (var childId in childrenByParent.GetValueOrDefault(parent.Id) ?? [])
            {
                // A process started before its "parent" belongs to an earlier owner of that PID (Windows keeps the
                // parent PID of orphans), so it is not a descendant.
                if (visited.Add(childId) && TryGetStartTime(childId) is { } startTime && startTime >= parent.StartTime)
                {
                    descendants.Add((childId, startTime));
                    pending.Enqueue((childId, startTime));
                }
            }
        }

        return descendants;
    }

    // Kills the process and whatever it started, unless the PID now belongs to another process.
    public static void Kill(int processId, DateTime startTime)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.StartTime == startTime)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already gone, or not ours to kill.
        }
    }

    private static IEnumerable<(int Id, int ParentId)> SnapshotParents() =>
        OperatingSystem.IsWindows() ? SnapshotWindows() : SnapshotLinux();

    private static List<(int Id, int ParentId)> SnapshotLinux()
    {
        var entries = new List<(int Id, int ParentId)>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var id))
            {
                continue;
            }

            try
            {
                // "pid (comm) state ppid ...": comm may contain spaces and parentheses, so parse after the last ')'.
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                entries.Add((id, int.Parse(fields[1])));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or FormatException or ArgumentOutOfRangeException
                                                  or IndexOutOfRangeException)
            {
                // The process exited while the snapshot was taken.
            }
        }

        return entries;
    }

    private static List<(int Id, int ParentId)> SnapshotWindows()
    {
        var entries = new List<(int Id, int ParentId)>();
        using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
        {
            return entries;
        }

        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        for (var found = Process32First(snapshot, ref entry); found; found = Process32Next(snapshot, ref entry))
        {
            entries.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
        }

        return entries;
    }

    private const uint SnapshotProcesses = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }
}
