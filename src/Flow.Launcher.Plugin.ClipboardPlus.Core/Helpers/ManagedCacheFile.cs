// ManagedCacheFile.cs
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Minimal provenance for a newly created cache file; no content or hashes.</summary>
public sealed record OwnedCacheFile(string FileName, uint Volume, uint IndexHigh, uint IndexLow, long CreationTime);

/// <summary>
/// Opens both files without sharing writes or deletion, verifies exact bytes and
/// file identity, and can delete the candidate by handle. The caller must check
/// all surviving references while holding its history and database boundaries.
/// </summary>
public sealed class ManagedCacheFile : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareRead = 1;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private readonly FileStream candidate;
    private readonly FileStream keeper;
    private readonly FileInformation candidateInfo;
    private readonly FileInformation keeperInfo;
    private readonly string candidatePath;
    private readonly string keeperPath;

    private ManagedCacheFile(FileStream candidate, FileStream keeper, FileInformation candidateInfo,
        FileInformation keeperInfo, string candidatePath, string keeperPath)
    {
        this.candidate = candidate;
        this.keeper = keeper;
        this.candidateInfo = candidateInfo;
        this.keeperInfo = keeperInfo;
        this.candidatePath = candidatePath;
        this.keeperPath = keeperPath;
    }

    /// <summary>Capture provenance while the newly created file is still exclusively open.</summary>
    public static OwnedCacheFile? IdentifyNewFile(string root, string path, FileStream file)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!IsManagedPng(root, path) || !TryGetSafeInformation(file.SafeFileHandle, path, out var info)) return null;
            return new OwnedCacheFile(Path.GetFileName(path), info.VolumeSerialNumber, info.FileIndexHigh,
                info.FileIndexLow, FileTime(info.CreationTimeHigh, info.CreationTimeLow));
        }
        catch (Exception) { return null; }
    }

    public static ManagedCacheFile? Prepare(string root, OwnedCacheFile ownership, string keeperPath)
    {
        if (!OperatingSystem.IsWindows() || ownership is null || string.IsNullOrEmpty(ownership.FileName) ||
            Path.GetFileName(ownership.FileName) != ownership.FileName) return null;
        FileStream? candidate = null;
        FileStream? keeper = null;
        try
        {
            var candidatePath = Path.Combine(root, ownership.FileName);
            if (!IsManagedPng(root, candidatePath) || !IsManagedPng(root, keeperPath) ||
                string.Equals(Path.GetFullPath(candidatePath), Path.GetFullPath(keeperPath), StringComparison.OrdinalIgnoreCase)) return null;
            candidate = OpenReadForDeletion(candidatePath);
            keeper = OpenRead(keeperPath);
            if (!TryGetSafeInformation(candidate.SafeFileHandle, candidatePath, out var candidateInfo) ||
                !TryGetSafeInformation(keeper.SafeFileHandle, keeperPath, out var keeperInfo) ||
                candidateInfo.VolumeSerialNumber != ownership.Volume || candidateInfo.FileIndexHigh != ownership.IndexHigh ||
                candidateInfo.FileIndexLow != ownership.IndexLow ||
                FileTime(candidateInfo.CreationTimeHigh, candidateInfo.CreationTimeLow) != ownership.CreationTime ||
                SameIdentity(candidateInfo, keeperInfo) || candidate.Length != keeper.Length || !SameBytes(candidate, keeper)) return null;
            var prepared = new ManagedCacheFile(candidate, keeper, candidateInfo, keeperInfo, candidatePath, keeperPath);
            candidate = null;
            keeper = null;
            return prepared;
        }
        catch (Exception) { return null; }
        finally { candidate?.Dispose(); keeper?.Dispose(); }
    }

    /// <summary>The callback must atomically confirm there are no surviving references.</summary>
    public bool DeleteIf(Func<bool> unreferenced)
    {
        try
        {
            if (!TryGetSafeInformation(candidate.SafeFileHandle, candidatePath, out var currentCandidate) ||
                !TryGetSafeInformation(keeper.SafeFileHandle, keeperPath, out var currentKeeper) ||
                !Unchanged(candidateInfo, currentCandidate) || !Unchanged(keeperInfo, currentKeeper) || !unreferenced()) return false;
            var disposition = new FileDispositionInfo { DeleteFile = 1 };
            return SetFileInformationByHandle(candidate.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>());
        }
        catch (Exception) { return false; }
    }

    public void Dispose() { candidate.Dispose(); keeper.Dispose(); }

    private static bool IsManagedPng(string root, string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);

    private static FileStream OpenReadForDeletion(string path) => Open(path, GenericRead | DeleteAccess);
    private static FileStream OpenRead(string path) => Open(path, GenericRead);
    private static FileStream Open(string path, uint access)
    {
        var handle = CreateFile(path, access, ShareRead, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Unable to open cache file safely."); }
        return new FileStream(handle, FileAccess.Read);
    }

    private static bool TryGetSafeInformation(SafeFileHandle handle, string expectedPath, out FileInformation info)
    {
        info = default;
        if (!GetFileInformationByHandle(handle, out info) || info.NumberOfLinks != 1 ||
            (info.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) actual = actual[4..];
        return string.Equals(Path.GetFullPath(expectedPath), actual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameBytes(Stream first, Stream second)
    {
        var left = new byte[65536];
        var right = new byte[65536];
        while (true)
        {
            var count = first.Read(left, 0, left.Length);
            var other = second.Read(right, 0, count);
            if (count != other || !left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, other))) return false;
            if (count == 0) return true;
        }
    }

    private static bool SameIdentity(FileInformation a, FileInformation b) =>
        a.VolumeSerialNumber == b.VolumeSerialNumber && a.FileIndexHigh == b.FileIndexHigh && a.FileIndexLow == b.FileIndexLow;
    private static bool Unchanged(FileInformation a, FileInformation b) => SameIdentity(a, b) &&
        a.CreationTimeHigh == b.CreationTimeHigh && a.CreationTimeLow == b.CreationTimeLow &&
        a.LastWriteTimeHigh == b.LastWriteTimeHigh && a.LastWriteTimeLow == b.LastWriteTimeLow &&
        a.FileSizeHigh == b.FileSizeHigh && a.FileSizeLow == b.FileSizeLow;
    private static long FileTime(uint high, uint low) => ((long)high << 32) | low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes, CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh, VolumeSerialNumber, FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo { public byte DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDispositionInfo information, uint size);
}
