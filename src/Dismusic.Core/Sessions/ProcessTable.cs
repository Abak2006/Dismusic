using System.Diagnostics;
using System.Runtime.InteropServices;
using Dismusic.Core.Interop;

namespace Dismusic.Core.Sessions;

public sealed record ProcessEntry(uint ProcessId, uint ParentProcessId, string ExeKey);

/// <summary>A point-in-time snapshot of running processes (pid, parent, lower-cased exe name).</summary>
public sealed class ProcessTable
{
    private readonly Dictionary<uint, ProcessEntry> _byPid;
    private readonly Dictionary<string, List<uint>> _byExe;

    public ProcessTable(IEnumerable<ProcessEntry> entries)
    {
        _byPid = new Dictionary<uint, ProcessEntry>();
        _byExe = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            _byPid[e.ProcessId] = e;
            if (!_byExe.TryGetValue(e.ExeKey, out var list))
                _byExe[e.ExeKey] = list = new List<uint>();
            list.Add(e.ProcessId);
        }
    }

    public static ProcessTable Capture()
    {
        var entries = new List<ProcessEntry>();
        var snapshot = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
        if (snapshot == Win32.INVALID_HANDLE_VALUE)
            throw new InvalidOperationException($"Could not list processes (Win32 error {Marshal.GetLastWin32Error()}).");
        try
        {
            var entry = new Win32.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Win32.PROCESSENTRY32W>() };
            if (Win32.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    entries.Add(new ProcessEntry(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile.ToLowerInvariant()));
                } while (Win32.Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }
        return new ProcessTable(entries);
    }

    public ProcessEntry? Get(uint pid) => _byPid.GetValueOrDefault(pid);

    public IReadOnlyList<uint> PidsForExe(string exeKey) =>
        _byExe.TryGetValue(exeKey, out var list) ? list : Array.Empty<uint>();

    public bool IsRunning(string exeKey) => _byExe.ContainsKey(exeKey);

    /// <summary>
    /// Walks up the parent chain while the parent has the same exe name, e.g. from Chrome's audio-service
    /// process to the main browser process. Used as the root for process-tree loopback capture.
    /// </summary>
    public uint ResolveRootOfSameExe(uint pid)
    {
        var current = Get(pid);
        if (current is null) return pid;
        for (int depth = 0; depth < 16; depth++)
        {
            var parent = Get(current.ParentProcessId);
            if (parent is null || parent.ProcessId == current.ProcessId ||
                !string.Equals(parent.ExeKey, current.ExeKey, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
        return current.ProcessId;
    }

    public static string? TryGetImagePath(uint pid)
    {
        var handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return Win32.QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    /// <summary>Human-readable app name from the executable's version info, falling back to the exe name.</summary>
    public static string DescribeExe(uint pid, string exeKey)
    {
        try
        {
            var path = TryGetImagePath(pid);
            if (path is not null)
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Fall through to the exe name.
        }
        return Path.GetFileNameWithoutExtension(exeKey);
    }
}
