using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevCleaner.Scanning;

internal interface IVolumeBoundary
{
    bool TryGetVolumeId(string path, out ulong volumeId, out string? error);
}

internal interface IFileSystemIdentityProvider : IVolumeBoundary
{
    bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error);
}

internal sealed class FileSystemIdentityProvider : IFileSystemIdentityProvider
{
    public bool TryGetVolumeId(string path, out ulong volumeId, out string? error)
    {
        if (TryGetIdentity(path, out var identity, out error) && identity is not null)
        {
            volumeId = identity.VolumeId;
            return true;
        }

        volumeId = 0;
        return false;
    }

    public bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            if (OperatingSystem.IsWindows()) return TryGetWindowsIdentity(path, attributes, info.LinkTarget, out identity, out error);
            if (OperatingSystem.IsLinux()) return TryGetLinuxIdentity(path, attributes, info.LinkTarget, out identity, out error);
            if (OperatingSystem.IsMacOS()) return TryGetMacIdentity(path, attributes, info.LinkTarget, out identity, out error);

            identity = null;
            error = $"Stable filesystem identity is unavailable on {RuntimeInformation.OSDescription}.";
            return false;
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            DllNotFoundException or
            EntryPointNotFoundException or
            MarshalDirectiveException or
            PlatformNotSupportedException)
        {
            identity = null;
            error = $"Unable to capture stable filesystem identity: {exception.Message}";
            return false;
        }
    }

    private static bool TryGetWindowsIdentity(
        string path,
        FileAttributes attributes,
        string? linkTarget,
        out FileSystemIdentity? identity,
        out string? error)
    {
        const uint shareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const uint openReparsePoint = 0x00200000;
        using var handle = CreateFileW(path, 0, shareReadWriteDelete, IntPtr.Zero, openExisting, backupSemantics | openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
        {
            identity = null;
            error = NativeError("Unable to capture stable Windows filesystem identity");
            return false;
        }

        identity = new FileSystemIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            attributes,
            linkTarget);
        error = null;
        return true;
    }

    private static bool TryGetLinuxIdentity(
        string path,
        FileAttributes attributes,
        string? linkTarget,
        out FileSystemIdentity? identity,
        out string? error)
    {
        const int atFileWorkingDirectory = -100;
        const int atSymlinkNoFollow = 0x100;
        const uint statxBasicStats = 0x07ff;
        const uint statxInode = 0x0100;
        if (Statx(atFileWorkingDirectory, path, atSymlinkNoFollow, statxBasicStats, out var information) != 0 ||
            (information.Mask & statxInode) == 0)
        {
            identity = null;
            error = NativeError("Unable to capture stable Linux filesystem identity");
            return false;
        }

        identity = new FileSystemIdentity(
            ((ulong)information.DeviceMajor << 32) | information.DeviceMinor,
            information.Inode,
            attributes,
            linkTarget);
        error = null;
        return true;
    }

    private static bool TryGetMacIdentity(
        string path,
        FileAttributes attributes,
        string? linkTarget,
        out FileSystemIdentity? identity,
        out string? error)
    {
        if (LStat(path, out var information) != 0)
        {
            identity = null;
            error = NativeError("Unable to capture stable macOS filesystem identity");
            return false;
        }

        identity = new FileSystemIdentity(unchecked((uint)information.Device), information.Inode, attributes, linkTarget);
        error = null;
        return true;
    }

    private static string NativeError(string prefix)
    {
        var errorCode = Marshal.GetLastPInvokeError();
        return $"{prefix}: {new Win32Exception(errorCode).Message} (error {errorCode}).";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxStatx information);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out MacStat information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public LinuxStatxTimestamp AccessTime;
        public LinuxStatxTimestamp BirthTime;
        public LinuxStatxTimestamp ChangeTime;
        public LinuxStatxTimestamp ModificationTime;
        public uint DeviceIdMajor;
        public uint DeviceIdMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlignment;
        public uint DirectIoOffsetAlignment;
        public ulong Spare00;
        public ulong Spare01;
        public ulong Spare02;
        public ulong Spare03;
        public ulong Spare04;
        public ulong Spare05;
        public ulong Spare06;
        public ulong Spare07;
        public ulong Spare08;
        public ulong Spare09;
        public ulong Spare10;
        public ulong Spare11;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacStat
    {
        public int Device;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint UserId;
        public uint GroupId;
        public int DeviceType;
        public MacTimespec AccessTime;
        public MacTimespec ModificationTime;
        public MacTimespec ChangeTime;
        public MacTimespec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Spare;
        public long Spare0;
        public long Spare1;
    }
}
