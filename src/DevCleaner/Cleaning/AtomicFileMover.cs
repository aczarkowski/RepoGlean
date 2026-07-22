using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DevCleaner.Cleaning;

internal interface IAtomicFileMover
{
    void MoveNoCopy(string sourcePath, string destinationPath);
}

internal sealed class NativeAtomicFileMover : IAtomicFileMover
{
    private const int AtFileWorkingDirectory = -100;
    private const uint LinuxRenameNoReplace = 1;
    private const uint MacRenameExclusive = 0x00000004;
    private const uint WindowsMoveFileWriteThrough = 0x00000008;

    public void MoveNoCopy(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            var succeeded = OperatingSystem.IsWindows()
                ? MoveFileExWindows(sourcePath, destinationPath, WindowsMoveFileWriteThrough)
                : OperatingSystem.IsLinux()
                    ? RenameAt2Linux(
                        AtFileWorkingDirectory,
                        sourcePath,
                        AtFileWorkingDirectory,
                        destinationPath,
                        LinuxRenameNoReplace) == 0
                    : OperatingSystem.IsMacOS() && RenameExclusiveMac(sourcePath, destinationPath, MacRenameExclusive) == 0;
            if (succeeded) return;

            var errorCode = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Atomic no-copy move from '{sourcePath}' to '{destinationPath}' failed: " +
                $"{new Win32Exception(errorCode).Message} (error {errorCode}).");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            throw new IOException("The platform atomic no-copy move primitive is unavailable.", exception);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExWindows(string existingFileName, string newFileName, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2Linux(
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
        uint flags);

    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusiveMac(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
        uint flags);
}
