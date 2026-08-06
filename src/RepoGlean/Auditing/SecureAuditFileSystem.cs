using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RepoGlean.Scanning;

namespace RepoGlean.Auditing;

internal enum SecureAuditCheckpoint
{
    BeforeDirectoryEnumeration,
    BeforeFileMeasurement,
}

internal sealed record SecureAuditIdentity(
    ulong VolumeId,
    string MountId,
    ulong FileIdLow,
    ulong FileIdHigh,
    FileSystemEntryKind Kind);

internal interface ISecureAuditEntry : IDisposable
{
    string Name { get; }

    string AbsolutePath { get; }

    FileSystemEntryKind Kind { get; }

    FileSystemMountIdentity MountIdentity { get; }

    SecureAuditIdentity Identity { get; }

    long Length { get; }

    DateTimeOffset? LastWriteTimeUtc { get; }

    string? InspectionError { get; }

    bool TryEnumerate(
        CancellationToken cancellationToken,
        out IReadOnlyList<ISecureAuditEntry> entries,
        out string? error);

    bool TryReopen(out ISecureAuditEntry? entry, out string? error);
}

internal interface ISecureAuditFileSystem
{
    bool TryOpenRoot(string absolutePath, out ISecureAuditEntry? root, out string? error);
}

internal sealed class SecureAuditFileSystem : ISecureAuditFileSystem
{
    private readonly IVolumeBoundary? mountOverride;
    private readonly IFileTimestampProvider? timestampOverride;

    internal SecureAuditFileSystem(
        IVolumeBoundary? mountOverride = null,
        IFileTimestampProvider? timestampOverride = null)
    {
        this.mountOverride = mountOverride;
        this.timestampOverride = timestampOverride;
    }

    public bool TryOpenRoot(string absolutePath, out ISecureAuditEntry? root, out string? error)
    {
        // Discovery supplies the repository root as the namespace trust anchor. The OS may resolve
        // aliases in its ancestors (for example macOS /var -> /private/var); the final root component
        // is opened without following it, and every audited descendant is opened relative to a held
        // directory handle with no-follow semantics and stable-identity rechecks.
        var normalizedRoot = NormalizeRootPath(absolutePath);
        if (OperatingSystem.IsWindows())
        {
            return WindowsSecureAuditEntry.TryOpenRoot(
                normalizedRoot,
                mountOverride,
                timestampOverride,
                out root,
                out error);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return UnixSecureAuditEntry.TryOpenRoot(
                normalizedRoot,
                mountOverride,
                timestampOverride,
                out root,
                out error);
        }

        root = null;
        error = $"Secure no-follow audit traversal is unavailable on {RuntimeInformation.OSDescription}.";
        return false;
    }

    internal static string NormalizeRootPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var filesystemRoot = Path.GetPathRoot(fullPath);
        if (filesystemRoot is not null &&
            string.Equals(
                fullPath,
                filesystemRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

internal sealed class UnixSecureAuditEntry : ISecureAuditEntry
{
    private const int AtSymlinkNoFollow = 0x100;
    private const int MacAtSymlinkNoFollow = 0x0020;
    private const int AtEmptyPath = 0x1000;
    internal const uint LinuxStatxInode = 0x0100;
    internal const uint LinuxRequiredAuditMetadata = 0x0002 | 0x0200 | 0x0040 | LinuxStatxInode | 0x1000;
    private readonly int descriptor;
    private readonly UnixSecureAuditEntry? parent;
    private readonly IVolumeBoundary? mountOverride;
    private readonly IFileTimestampProvider? timestampOverride;
    private bool disposed;

    private UnixSecureAuditEntry(
        string name,
        string absolutePath,
        int descriptor,
        UnixSecureAuditEntry? parent,
        SecureAuditIdentity identity,
        FileSystemMountIdentity mountIdentity,
        long length,
        DateTimeOffset? lastWriteTimeUtc,
        IVolumeBoundary? mountOverride,
        IFileTimestampProvider? timestampOverride)
    {
        Name = name;
        AbsolutePath = absolutePath;
        this.descriptor = descriptor;
        this.parent = parent;
        Identity = identity;
        MountIdentity = mountIdentity;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
        this.mountOverride = mountOverride;
        this.timestampOverride = timestampOverride;
    }

    public string Name { get; }

    public string AbsolutePath { get; }

    public FileSystemEntryKind Kind => Identity.Kind;

    public FileSystemMountIdentity MountIdentity { get; }

    public SecureAuditIdentity Identity { get; }

    public long Length { get; }

    public DateTimeOffset? LastWriteTimeUtc { get; }

    public string? InspectionError => null;

    internal static bool TryOpenRoot(
        string absolutePath,
        IVolumeBoundary? mountOverride,
        IFileTimestampProvider? timestampOverride,
        out ISecureAuditEntry? root,
        out string? error)
    {
        var descriptor = Open(absolutePath, DirectoryOpenFlags());
        if (descriptor < 0)
        {
            root = null;
            error = NativeError("Unable to securely open the audit repository root");
            return false;
        }

        if (!TryCreate(
                Path.GetFileName(absolutePath),
                absolutePath,
                descriptor,
                parent: null,
                mountOverride,
                timestampOverride,
                out var opened,
                out error))
        {
            Close(descriptor);
            root = null;
            return false;
        }

        if (opened!.Kind != FileSystemEntryKind.Directory)
        {
            opened.Dispose();
            root = null;
            error = "The securely opened audit repository root is not a directory.";
            return false;
        }

        root = opened;
        return true;
    }

    public bool TryEnumerate(
        CancellationToken cancellationToken,
        out IReadOnlyList<ISecureAuditEntry> entries,
        out string? error)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || Kind != FileSystemEntryKind.Directory)
        {
            entries = [];
            error = "The secure audit directory handle is unavailable.";
            return false;
        }

        var enumerationDescriptor = OpenAt(descriptor, ".", DirectoryOpenFlags());
        if (enumerationDescriptor < 0)
        {
            entries = [];
            error = NativeError("Unable to duplicate the secure audit directory handle for enumeration");
            return false;
        }

        var directory = FdOpenDir(enumerationDescriptor);
        if (directory == IntPtr.Zero)
        {
            Close(enumerationDescriptor);
            entries = [];
            error = NativeError("Unable to enumerate the secure audit directory handle");
            return false;
        }

        var openedEntries = new List<ISecureAuditEntry>();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.SetLastPInvokeError(0);
                var nativeEntry = ReadDir(directory);
                cancellationToken.ThrowIfCancellationRequested();
                if (nativeEntry == IntPtr.Zero)
                {
                    var errorCode = Marshal.GetLastPInvokeError();
                    if (errorCode != 0)
                    {
                        DisposeAll(openedEntries);
                        entries = [];
                        error = NativeError("Unable to read the secure audit directory handle", errorCode);
                        return false;
                    }

                    break;
                }

                var name = ReadDirectoryEntryName(nativeEntry);
                if (name is "." or "..") continue;
                if (!TrySnapshotChild(name, out var child, out var childError))
                {
                    openedEntries.Add(new UnavailableSecureAuditEntry(
                        name,
                        Path.Combine(AbsolutePath, name),
                        childError ?? "Unable to securely inspect the audit entry."));
                    continue;
                }

                openedEntries.Add(child!);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            DisposeAll(openedEntries);
            entries = [];
            error = null;
            throw;
        }
        finally
        {
            CloseDir(directory);
        }

        entries = openedEntries;
        error = null;
        return true;
    }

    public bool TryReopen(out ISecureAuditEntry? entry, out string? error)
    {
        if (disposed)
        {
            entry = null;
            error = "The secure audit entry handle is unavailable.";
            return false;
        }

        return parent is null
            ? TryOpenRoot(AbsolutePath, mountOverride, timestampOverride, out entry, out error)
            : parent.TryOpenChild(Name, out entry, out error);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (descriptor >= 0) Close(descriptor);
    }

    private bool TryOpenChild(string name, out ISecureAuditEntry? entry, out string? error)
    {
        var childPath = Path.Combine(AbsolutePath, name);
        var descriptor = OpenAt(this.descriptor, name, GenericOpenFlags());
        if (descriptor < 0)
        {
            var errorCode = Marshal.GetLastPInvokeError();
            if (errorCode == LoopError())
            {
                entry = new UnixSecureAuditEntry(
                    name,
                    childPath,
                    -1,
                    this,
                    new SecureAuditIdentity(0, "link", 0, 0, FileSystemEntryKind.Link),
                    new FileSystemMountIdentity(0, "link"),
                    0,
                    null,
                    mountOverride,
                    timestampOverride);
                error = null;
                return true;
            }

            entry = null;
            error = NativeError($"Unable to securely open audit entry '{childPath}'", errorCode);
            return false;
        }

        UnixSecureAuditEntry? opened;
        try
        {
            if (!TryCreate(
                    name,
                    childPath,
                    descriptor,
                    this,
                    mountOverride,
                    timestampOverride,
                    out opened,
                    out error))
            {
                Close(descriptor);
                entry = null;
                return false;
            }
        }
        catch
        {
            Close(descriptor);
            throw;
        }

        entry = opened;
        return true;
    }

    private bool TrySnapshotChild(string name, out ISecureAuditEntry? entry, out string? error)
    {
        var childPath = Path.Combine(AbsolutePath, name);
        SecureAuditIdentity identity;
        FileSystemMountIdentity mountIdentity;
        long length;
        DateTimeOffset? timestamp;
        if (OperatingSystem.IsLinux())
        {
            if (Statx(descriptor, name, AtSymlinkNoFollow, LinuxRequiredAuditMetadata, out var information) != 0)
            {
                entry = null;
                error = NativeError($"Unable to securely inspect audit entry '{childPath}'");
                return false;
            }

            if (!TryConvertLinuxMetadata(
                    information,
                    out identity,
                    out mountIdentity,
                    out length,
                    out timestamp,
                    out error))
            {
                entry = null;
                return false;
            }
        }
        else
        {
            MacStat information = default;
            var result = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => FStatAtMacArm64(descriptor, name, out information, MacAtSymlinkNoFollow),
                Architecture.X64 => FStatAtMacX64(descriptor, name, out information, MacAtSymlinkNoFollow),
                _ => -1,
            };
            if (result != 0)
            {
                entry = null;
                error = RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64)
                    ? $"Secure audit traversal is unavailable on macOS architecture {RuntimeInformation.ProcessArchitecture}."
                    : NativeError($"Unable to securely inspect audit entry '{childPath}'");
                return false;
            }

            ConvertMacMetadata(information, out identity, out mountIdentity, out length, out timestamp);
            error = null;
        }

        if (!ApplyOverrides(
                childPath,
                mountOverride,
                timestampOverride,
                ref identity,
                ref mountIdentity,
                ref timestamp,
                out error))
        {
            entry = null;
            return false;
        }

        entry = new UnixSecureAuditEntry(
            name,
            childPath,
            -1,
            this,
            identity,
            mountIdentity,
            length,
            timestamp,
            mountOverride,
            timestampOverride);
        return true;
    }

    private static bool TryCreate(
        string name,
        string absolutePath,
        int descriptor,
        UnixSecureAuditEntry? parent,
        IVolumeBoundary? mountOverride,
        IFileTimestampProvider? timestampOverride,
        out UnixSecureAuditEntry? entry,
        out string? error)
    {
        if (!TryReadMetadata(descriptor, out var identity, out var mountIdentity, out var length, out var timestamp, out error))
        {
            entry = null;
            return false;
        }

        if (!ApplyOverrides(
                absolutePath,
                mountOverride,
                timestampOverride,
                ref identity,
                ref mountIdentity,
                ref timestamp,
                out error))
        {
            entry = null;
            return false;
        }

        entry = new UnixSecureAuditEntry(
            name,
            absolutePath,
            descriptor,
            parent,
            identity,
            mountIdentity,
            length,
            timestamp,
            mountOverride,
            timestampOverride);
        error = null;
        return true;
    }

    private static bool ApplyOverrides(
        string absolutePath,
        IVolumeBoundary? mountOverride,
        IFileTimestampProvider? timestampOverride,
        ref SecureAuditIdentity identity,
        ref FileSystemMountIdentity mountIdentity,
        ref DateTimeOffset? timestamp,
        out string? error)
    {
        if (mountOverride is not null)
        {
            if (!mountOverride.TryGetMountIdentity(absolutePath, out var overriddenMount, out error) || overriddenMount is null)
            {
                return false;
            }

            mountIdentity = overriddenMount;
            identity = identity with { VolumeId = overriddenMount.VolumeId, MountId = overriddenMount.MountId };
        }

        if (timestampOverride is not null)
        {
            timestamp = timestampOverride.TryGetLastWriteTimeUtc(absolutePath, out var overriddenTimestamp)
                ? overriddenTimestamp
                : null;
        }

        error = null;
        return true;
    }

    private static bool TryReadMetadata(
        int descriptor,
        out SecureAuditIdentity identity,
        out FileSystemMountIdentity mountIdentity,
        out long length,
        out DateTimeOffset? timestamp,
        out string? error)
    {
        if (OperatingSystem.IsLinux())
        {
            if (Statx(descriptor, string.Empty, AtEmptyPath, LinuxRequiredAuditMetadata, out var information) != 0)
            {
                identity = default!;
                mountIdentity = default!;
                length = 0;
                timestamp = null;
                error = NativeError("Unable to inspect a secure Linux audit handle");
                return false;
            }

            return TryConvertLinuxMetadata(
                information,
                out identity,
                out mountIdentity,
                out length,
                out timestamp,
                out error);
        }

        MacStat macInformation = default;
        var result = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => FStatMacArm64(descriptor, out macInformation),
            Architecture.X64 => FStatMacX64(descriptor, out macInformation),
            _ => -1,
        };
        if (result != 0)
        {
            identity = default!;
            mountIdentity = default!;
            length = 0;
            timestamp = null;
            error = RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64)
                ? $"Secure audit traversal is unavailable on macOS architecture {RuntimeInformation.ProcessArchitecture}."
                : NativeError("Unable to inspect a secure macOS audit handle");
            return false;
        }

        ConvertMacMetadata(macInformation, out identity, out mountIdentity, out length, out timestamp);
        error = null;
        return true;
    }

    private static bool TryConvertLinuxMetadata(
        LinuxStatx information,
        out SecureAuditIdentity identity,
        out FileSystemMountIdentity mountIdentity,
        out long length,
        out DateTimeOffset? timestamp,
        out string? error)
    {
        if (!HasRequiredLinuxAuditMetadata(information.Mask))
        {
            identity = default!;
            mountIdentity = default!;
            length = 0;
            timestamp = null;
            error = $"Linux statx did not return required secure audit metadata fields (mask 0x{information.Mask:x}).";
            return false;
        }

        var kind = GetUnixEntryKind(information.Mode);
        var volumeId = ((ulong)information.DeviceMajor << 32) | information.DeviceMinor;
        var mountId = information.MountId.ToString(CultureInfo.InvariantCulture);
        identity = new SecureAuditIdentity(volumeId, mountId, information.Inode, 0, kind);
        mountIdentity = new FileSystemMountIdentity(volumeId, mountId);
        length = information.Size > long.MaxValue ? long.MaxValue : (long)information.Size;
        timestamp = FromUnixTime(information.ModificationTime.Seconds, information.ModificationTime.Nanoseconds);
        error = null;
        return true;
    }

    internal static bool HasRequiredLinuxAuditMetadata(uint mask) =>
        (mask & LinuxRequiredAuditMetadata) == LinuxRequiredAuditMetadata;

    private static void ConvertMacMetadata(
        MacStat information,
        out SecureAuditIdentity identity,
        out FileSystemMountIdentity mountIdentity,
        out long length,
        out DateTimeOffset? timestamp)
    {
        var kind = GetUnixEntryKind(information.Mode);
        var volumeId = unchecked((uint)information.Device);
        var mountId = $"darwin-device:{volumeId.ToString(CultureInfo.InvariantCulture)}";
        identity = new SecureAuditIdentity(volumeId, mountId, information.Inode, 0, kind);
        mountIdentity = new FileSystemMountIdentity(volumeId, mountId);
        length = Math.Max(0, information.Size);
        timestamp = FromUnixTime(information.ModificationTime.Seconds, information.ModificationTime.Nanoseconds);
    }

    private static DateTimeOffset? FromUnixTime(long seconds, long nanoseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanoseconds / 100);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static FileSystemEntryKind GetUnixEntryKind(ushort mode) =>
        (mode & 0xf000) switch
        {
            0x8000 => FileSystemEntryKind.RegularFile,
            0x4000 => FileSystemEntryKind.Directory,
            0xa000 => FileSystemEntryKind.Link,
            _ => FileSystemEntryKind.Other,
        };

    private static string ReadDirectoryEntryName(IntPtr entry)
    {
        var nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
        var maximumLength = OperatingSystem.IsMacOS() ? 1024 : 256;
        var bytes = new byte[maximumLength];
        Marshal.Copy(IntPtr.Add(entry, nameOffset), bytes, 0, bytes.Length);
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static int GenericOpenFlags() => OperatingSystem.IsMacOS()
        ? 0x0004 | 0x0100 | 0x01000000
        : 0x0800 | 0x20000 | 0x80000;

    private static int DirectoryOpenFlags() => OperatingSystem.IsMacOS()
        ? GenericOpenFlags() | 0x00100000
        : GenericOpenFlags() | 0x10000;

    private static int LoopError() => OperatingSystem.IsMacOS() ? 62 : 40;

    private static string NativeError(string prefix, int? capturedError = null)
    {
        var errorCode = capturedError ?? Marshal.GetLastPInvokeError();
        return $"{prefix}: {new Win32Exception(errorCode).Message} (error {errorCode}).";
    }

    private static void DisposeAll(IEnumerable<ISecureAuditEntry> entries)
    {
        foreach (var entry in entries) entry.Dispose();
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr FdOpenDir(int descriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr ReadDir(IntPtr directory);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDir(IntPtr directory);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxStatx information);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatMacArm64(int descriptor, out MacStat information);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatMacX64(int descriptor, out MacStat information);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatAtMacArm64(
        int descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out MacStat information,
        int flags);

    [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    private static extern int FStatAtMacX64(
        int descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out MacStat information,
        int flags);

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

internal sealed class WindowsSecureAuditEntry : ISecureAuditEntry
{
    private const uint FileReadData = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint ShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint ObjCaseInsensitive = 0x00000040;
    private readonly SafeFileHandle handle;
    private readonly WindowsSecureAuditEntry? parent;
    private readonly IVolumeBoundary? mountOverride;
    private readonly IFileTimestampProvider? timestampOverride;

    private WindowsSecureAuditEntry(
        string name,
        string absolutePath,
        SafeFileHandle handle,
        WindowsSecureAuditEntry? parent,
        SecureAuditIdentity identity,
        FileSystemMountIdentity mountIdentity,
        long length,
        DateTimeOffset? lastWriteTimeUtc,
        IVolumeBoundary? mountOverride,
        IFileTimestampProvider? timestampOverride)
    {
        Name = name;
        AbsolutePath = absolutePath;
        this.handle = handle;
        this.parent = parent;
        Identity = identity;
        MountIdentity = mountIdentity;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
        this.mountOverride = mountOverride;
        this.timestampOverride = timestampOverride;
    }

    public string Name { get; }

    public string AbsolutePath { get; }

    public FileSystemEntryKind Kind => Identity.Kind;

    public FileSystemMountIdentity MountIdentity { get; }

    public SecureAuditIdentity Identity { get; }

    public long Length { get; }

    public DateTimeOffset? LastWriteTimeUtc { get; }

    public string? InspectionError => null;

    internal static bool TryOpenRoot(
        string absolutePath,
        IVolumeBoundary? mountOverride,
        IFileTimestampProvider? timestampOverride,
        out ISecureAuditEntry? root,
        out string? error)
    {
        var handle = CreateFileW(
            absolutePath,
            FileReadData | FileReadAttributes,
            ShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics | OpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            root = null;
            error = NativeError("Unable to securely open the audit repository root");
            return false;
        }

        if (!TryCreate(
                Path.GetFileName(absolutePath),
                absolutePath,
                handle,
                parent: null,
                mountOverride,
                timestampOverride,
                out var opened,
                out error))
        {
            handle.Dispose();
            root = null;
            return false;
        }

        if (opened!.Kind != FileSystemEntryKind.Directory)
        {
            opened.Dispose();
            root = null;
            error = "The securely opened audit repository root is not a directory.";
            return false;
        }

        root = opened;
        return true;
    }

    public bool TryEnumerate(
        CancellationToken cancellationToken,
        out IReadOnlyList<ISecureAuditEntry> entries,
        out string? error)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (handle.IsClosed || handle.IsInvalid || Kind != FileSystemEntryKind.Directory)
        {
            entries = [];
            error = "The secure audit directory handle is unavailable.";
            return false;
        }

        const int fileIdBothDirectoryInfo = 10;
        const int errorNoMoreFiles = 18;
        var buffer = new byte[64 * 1024];
        var openedEntries = new List<ISecureAuditEntry>();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var enumerated = GetFileInformationByHandleEx(
                    handle,
                    fileIdBothDirectoryInfo,
                    buffer,
                    (uint)buffer.Length);
                cancellationToken.ThrowIfCancellationRequested();
                if (!enumerated)
                {
                    var errorCode = Marshal.GetLastPInvokeError();
                    if (errorCode == errorNoMoreFiles) break;
                    DisposeAll(openedEntries);
                    entries = [];
                    error = NativeError("Unable to enumerate the secure Windows audit directory handle", errorCode);
                    return false;
                }

                var offset = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var nextOffset = BitConverter.ToUInt32(buffer, offset);
                    var attributes = BitConverter.ToUInt32(buffer, offset + 56);
                    var rawNameLength = BitConverter.ToUInt32(buffer, offset + 60);
                    if (rawNameLength > buffer.Length - offset - 104)
                    {
                        DisposeAll(openedEntries);
                        entries = [];
                        error = "Windows returned malformed handle-relative directory enumeration data.";
                        return false;
                    }

                    var nameLength = (int)rawNameLength;
                    var name = Encoding.Unicode.GetString(buffer, offset + 104, nameLength);
                    if (name is not ("." or ".."))
                    {
                        if (!TrySnapshotChild(
                                name,
                                attributes,
                                out var child,
                                out var childError))
                        {
                            openedEntries.Add(new UnavailableSecureAuditEntry(
                                name,
                                Path.Combine(AbsolutePath, name),
                                childError ?? "Unable to securely inspect the audit entry."));
                        }
                        else
                        {
                            openedEntries.Add(child!);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (nextOffset == 0) break;
                    if (nextOffset > int.MaxValue || nextOffset > buffer.Length - offset)
                    {
                        DisposeAll(openedEntries);
                        entries = [];
                        error = "Windows returned malformed handle-relative directory enumeration data.";
                        return false;
                    }

                    offset += (int)nextOffset;
                    if (offset < 0 || offset + 104 > buffer.Length)
                    {
                        DisposeAll(openedEntries);
                        entries = [];
                        error = "Windows returned malformed handle-relative directory enumeration data.";
                        return false;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            DisposeAll(openedEntries);
            entries = [];
            error = null;
            throw;
        }

        entries = openedEntries;
        error = null;
        return true;
    }

    public bool TryReopen(out ISecureAuditEntry? entry, out string? error)
    {
        if (parent is not null)
        {
            return parent.TryOpenChild(Name, Kind, out entry, out error);
        }

        if (handle.IsClosed || handle.IsInvalid)
        {
            entry = null;
            error = "The secure audit entry handle is unavailable.";
            return false;
        }

        return TryOpenRoot(AbsolutePath, mountOverride, timestampOverride, out entry, out error);
    }

    public void Dispose() => handle.Dispose();

    private bool TryOpenChild(
        string name,
        FileSystemEntryKind expectedKind,
        out ISecureAuditEntry? entry,
        out string? error)
    {
        var childPath = Path.Combine(AbsolutePath, name);
        var desiredAccess = FileReadAttributes | Synchronize |
            (expectedKind == FileSystemEntryKind.Directory ? FileReadData : 0);
        var createOptions = FileSynchronousIoNonAlert | OpenReparsePoint;
        if (expectedKind == FileSystemEntryKind.Directory)
        {
            createOptions |= FileDirectoryFile;
        }
        else if (expectedKind != FileSystemEntryKind.Link)
        {
            createOptions |= FileNonDirectoryFile;
        }

        if (!TryNtOpenRelative(handle, name, desiredAccess, createOptions, out var childHandle, out error))
        {
            entry = null;
            return false;
        }

        WindowsSecureAuditEntry? opened;
        try
        {
            if (!TryCreate(
                    name,
                    childPath,
                    childHandle!,
                    this,
                    mountOverride,
                    timestampOverride,
                    out opened,
                    out error))
            {
                childHandle!.Dispose();
                entry = null;
                return false;
            }
        }
        catch
        {
            childHandle!.Dispose();
            throw;
        }

        entry = opened;
        return true;
    }

    private bool TrySnapshotChild(
        string name,
        uint rawAttributes,
        out ISecureAuditEntry? entry,
        out string? error)
    {
        var attributes = (FileAttributes)rawAttributes;
        var expectedKind = (attributes & FileAttributes.ReparsePoint) != 0
            ? FileSystemEntryKind.Link
            : (attributes & FileAttributes.Directory) != 0
                ? FileSystemEntryKind.Directory
                : (attributes & FileAttributes.Device) != 0
                    ? FileSystemEntryKind.Other
                    : FileSystemEntryKind.RegularFile;
        if (!TryOpenChild(name, expectedKind, out var opened, out error) || opened is null)
        {
            entry = null;
            return false;
        }

        using (opened)
        {
            entry = new WindowsSecureAuditEntry(
                name,
                opened.AbsolutePath,
                new SafeFileHandle(IntPtr.Zero, ownsHandle: false),
                this,
                opened.Identity,
                opened.MountIdentity,
                opened.Length,
                opened.LastWriteTimeUtc,
                mountOverride,
                timestampOverride);
            error = null;
            return true;
        }
    }

    private static bool TryCreate(
        string name,
        string absolutePath,
        SafeFileHandle handle,
        WindowsSecureAuditEntry? parent,
        IVolumeBoundary? mountOverride,
        IFileTimestampProvider? timestampOverride,
        out WindowsSecureAuditEntry? entry,
        out string? error)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            entry = null;
            error = NativeError("Unable to inspect a secure Windows audit handle");
            return false;
        }

        var attributes = (FileAttributes)information.FileAttributes;
        var kind = (attributes & FileAttributes.ReparsePoint) != 0
            ? FileSystemEntryKind.Link
            : (attributes & FileAttributes.Directory) != 0
                ? FileSystemEntryKind.Directory
                : (attributes & FileAttributes.Device) != 0
                    ? FileSystemEntryKind.Other
                    : FileSystemEntryKind.RegularFile;
        if (!TryReadAuthoritativeIdentity(handle, kind, out var identity, out error) || identity is null)
        {
            entry = null;
            return false;
        }

        var volumeId = identity.VolumeId;
        var mountId = identity.MountId;
        var mountIdentity = new FileSystemMountIdentity(volumeId, mountId);
        if (mountOverride is not null)
        {
            if (!mountOverride.TryGetMountIdentity(absolutePath, out var overriddenMount, out error) || overriddenMount is null)
            {
                entry = null;
                return false;
            }

            mountIdentity = overriddenMount;
            identity = identity with { VolumeId = overriddenMount.VolumeId, MountId = overriddenMount.MountId };
        }

        DateTimeOffset? timestamp;
        if (timestampOverride is not null)
        {
            timestamp = timestampOverride.TryGetLastWriteTimeUtc(absolutePath, out var overriddenTimestamp)
                ? overriddenTimestamp
                : null;
        }
        else
        {
            try
            {
                timestamp = DateTimeOffset.FromFileTime(CombineFileTime(information.LastWriteTime));
            }
            catch (ArgumentOutOfRangeException)
            {
                timestamp = null;
            }
        }

        var rawFileSize = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        entry = new WindowsSecureAuditEntry(
            name,
            absolutePath,
            handle,
            parent,
            identity,
            mountIdentity,
            rawFileSize > long.MaxValue ? long.MaxValue : (long)rawFileSize,
            timestamp,
            mountOverride,
            timestampOverride);
        error = null;
        return true;
    }

    private static bool TryReadAuthoritativeIdentity(
        SafeFileHandle handle,
        FileSystemEntryKind kind,
        out SecureAuditIdentity? identity,
        out string? error)
    {
        const int fileIdInfo = 18;
        var buffer = new byte[24];
        if (!GetFileInformationByHandleEx(handle, fileIdInfo, buffer, (uint)buffer.Length))
        {
            identity = null;
            error = NativeError("Unable to read authoritative Windows file identity");
            return false;
        }

        var volumeSerialNumber = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
        if (!TryCreateAuthoritativeIdentity(volumeSerialNumber, buffer.AsSpan(8, 16), kind, out identity))
        {
            error = "Windows returned an unavailable or zero authoritative 128-bit file identity.";
            return false;
        }

        error = null;
        return true;
    }

    internal static bool TryCreateAuthoritativeIdentity(
        ulong volumeSerialNumber,
        ReadOnlySpan<byte> fileId,
        FileSystemEntryKind kind,
        out SecureAuditIdentity? identity)
    {
        if (volumeSerialNumber == 0 || fileId.Length != 16)
        {
            identity = null;
            return false;
        }

        var low = BinaryPrimitives.ReadUInt64LittleEndian(fileId[..8]);
        var high = BinaryPrimitives.ReadUInt64LittleEndian(fileId[8..]);
        if (low == 0 && high == 0)
        {
            identity = null;
            return false;
        }

        identity = new SecureAuditIdentity(
            volumeSerialNumber,
            $"windows-volume:{volumeSerialNumber.ToString(CultureInfo.InvariantCulture)}",
            low,
            high,
            kind);
        return true;
    }

    private static bool TryNtOpenRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint createOptions,
        out SafeFileHandle? handle,
        out string? error)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicode = new UnicodeString
        {
            Length = checked((ushort)(name.Length * 2)),
            MaximumLength = checked((ushort)(name.Length * 2)),
            Buffer = nameBuffer,
        };
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(unicode, unicodePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjCaseInsensitive,
            };
            var status = NtCreateFile(
                out var rawHandle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                0,
                ShareReadWriteDelete,
                FileOpen,
                createOptions,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                handle = null;
                var errorCode = unchecked((int)RtlNtStatusToDosError(status));
                error = $"Unable to securely open handle-relative Windows audit entry: {new Win32Exception(errorCode).Message} (error {errorCode}).";
                return false;
            }

            handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            error = null;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static long CombineFileTime(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        unchecked(((long)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime);

    private static string NativeError(string prefix, int? capturedError = null)
    {
        var errorCode = capturedError ?? Marshal.GetLastPInvokeError();
        return $"{prefix}: {new Win32Exception(errorCode).Message} (error {errorCode}).";
    }

    private static void DisposeAll(IEnumerable<ISecureAuditEntry> entries)
    {
        foreach (var entry in entries) entry.Dispose();
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
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        [Out] byte[] fileInformation,
        uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

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
}

internal sealed class UnavailableSecureAuditEntry(
    string name,
    string absolutePath,
    string inspectionError) : ISecureAuditEntry
{
    public string Name { get; } = name;

    public string AbsolutePath { get; } = absolutePath;

    public FileSystemEntryKind Kind => FileSystemEntryKind.Other;

    public FileSystemMountIdentity MountIdentity { get; } = new(0, "unavailable");

    public SecureAuditIdentity Identity { get; } = new(0, "unavailable", 0, 0, FileSystemEntryKind.Other);

    public long Length => 0;

    public DateTimeOffset? LastWriteTimeUtc => null;

    public string? InspectionError { get; } = inspectionError;

    public bool TryEnumerate(
        CancellationToken cancellationToken,
        out IReadOnlyList<ISecureAuditEntry> entries,
        out string? error)
    {
        cancellationToken.ThrowIfCancellationRequested();
        entries = [];
        error = InspectionError;
        return false;
    }

    public bool TryReopen(out ISecureAuditEntry? entry, out string? error)
    {
        entry = null;
        error = InspectionError;
        return false;
    }

    public void Dispose()
    {
    }
}
