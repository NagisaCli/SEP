using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SEP.App.Services;

/// <summary>
/// Lists the disk files that given local processes hold open, the way Process Explorer does: the system handle
/// table is read once, only file handles of those processes are duplicated, and each is resolved to its path
/// (\\server\share\... for network files). Only the current user's own processes can be inspected, which is exactly
/// what SEP needs (the E3D sessions of this user on this PC). Nothing here touches a file server beyond resolving
/// names of files E3D already has open.
/// </summary>
internal static class OpenFileProbe
{
    private const int SystemExtendedHandleInformation = 64;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x2;
    private const uint FileTypeDisk = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public IntPtr Object;
        public UIntPtr UniqueProcessId;
        public UIntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr file);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr file, StringBuilder path, uint length, uint flags);

    /// <summary>Paths of disk files the processes hold open, or null when the handle table could not be read.</summary>
    public static HashSet<string>? OpenFiles(IReadOnlyCollection<int> pids)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pids.Count == 0) return result;

        // Own file handle: tells which object type index means "File" on this Windows build, so handles of other
        // types (named pipes behind sync I/O, events, …) are never duplicated or queried.
        using var marker = new FileStream(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SEP.exe"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        IntPtr markerHandle = marker.SafeFileHandle.DangerousGetHandle();
        int me = Environment.ProcessId;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 1 << 20;
            while (true)
            {
                buffer = Marshal.AllocHGlobal(size);
                uint status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
                if (status == 0) break;
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != StatusInfoLengthMismatch || size >= 512 << 20) return null;
                size = Math.Max(size * 2, needed + (1 << 16));
            }

            long count = (long)(ulong)Marshal.ReadIntPtr(buffer);
            int entrySize = Marshal.SizeOf<HandleEntry>();
            IntPtr first = buffer + 2 * IntPtr.Size;

            ushort fileType = 0;
            for (long i = 0; i < count; i++)
            {
                var e = Marshal.PtrToStructure<HandleEntry>(first + (int)(i * entrySize));
                if ((int)e.UniqueProcessId == me && e.HandleValue == (UIntPtr)(ulong)markerHandle) { fileType = e.ObjectTypeIndex; break; }
            }
            if (fileType == 0) return null;

            var wanted = new HashSet<int>(pids);
            var processes = new Dictionary<int, IntPtr>();
            var self = GetCurrentProcess();
            var path = new StringBuilder(1024);
            try
            {
                for (long i = 0; i < count; i++)
                {
                    var e = Marshal.PtrToStructure<HandleEntry>(first + (int)(i * entrySize));
                    int pid = (int)e.UniqueProcessId;
                    if (e.ObjectTypeIndex != fileType || !wanted.Contains(pid)) continue;

                    if (!processes.TryGetValue(pid, out var process))
                    {
                        process = OpenProcess(ProcessDupHandle, false, pid);
                        processes[pid] = process;
                    }
                    if (process == IntPtr.Zero) continue;

                    if (!DuplicateHandle(process, (IntPtr)(long)(ulong)e.HandleValue, self, out var dup, 0, false, DuplicateSameAccess)) continue;
                    try
                    {
                        if (GetFileType(dup) != FileTypeDisk) continue;
                        path.Clear();
                        uint n = GetFinalPathNameByHandleW(dup, path, (uint)path.Capacity, 0);
                        if (n == 0 || n >= path.Capacity) continue;
                        result.Add(Clean(path.ToString()));
                    }
                    finally
                    {
                        CloseHandle(dup);
                    }
                }
            }
            finally
            {
                foreach (var h in processes.Values) if (h != IntPtr.Zero) CloseHandle(h);
            }
            return result;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or AccessViolationException or IOException or UnauthorizedAccessException)
        {
            App.Log($"Open-file probe failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>\\?\UNC\server\share\x → \\server\share\x, \\?\D:\x → D:\x.</summary>
    private static string Clean(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + p[8..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p[4..];
        return p;
    }
}
