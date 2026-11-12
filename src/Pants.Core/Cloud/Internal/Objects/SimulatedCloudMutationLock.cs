using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.IO;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Cloud.Internal.Objects;

/// <summary>Shares Midge's fixed native mutation stripes across processes and engines.</summary>
sealed class SimulatedCloudMutationLock : IDisposable
{
    readonly FileStream _stream;
    int _disposed;

    SimulatedCloudMutationLock(FileStream stream) => _stream = stream;

    public static SimulatedCloudMutationLock Acquire(
        string root,
        string fullPath,
        CancellationToken cancellationToken,
        Action? onContention = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stripe = DiskFormat.Crc32C(Encoding.UTF8.GetBytes(fullPath)) % 64;
        var directory = Path.Combine(root, ".midge-locks");
        AtomicStagedFile.CreateDirectoryDurably(directory);
        var path = Path.Combine(directory, $"stripe-{stripe:x2}.lock");
        var stream = OpenStripe(path);
        try
        {
            var delayMilliseconds = 1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        stream.Lock(0, 1);
                        return new SimulatedCloudMutationLock(stream);
                    }
                    catch (IOException exception) when ((exception.HResult & 0xffff) == 33)
                    {
                        // A different handle owns the overlapping mutation stripe.
                    }
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                {
                    if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) == 0)
                    {
                        return new SimulatedCloudMutationLock(stream);
                    }

                    var error = Marshal.GetLastPInvokeError();
                    if (error == 4)
                    {
                        continue;
                    }

                    if (error is not 11 and not 35)
                    {
                        throw new IOException($"Could not acquire simulated object mutation stripe: {new Win32Exception(error).Message}", error);
                    }
                }
                else
                {
                    throw new PantsNotSupportedException("Simulated cloud requires native conditional mutation locks on this platform.");
                }

                onContention?.Invoke();
                Thread.Sleep(delayMilliseconds);
                delayMilliseconds = Math.Min(delayMilliseconds * 2, 10);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                _stream.Unlock(0, 1);
            }
            else
            {
                _ = Flock(_stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 8);
            }
        }
        finally
        {
            _stream.Dispose();
        }
    }

    static FileStream OpenStripe(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.ReadWrite, 1, FileOptions.None);
        }

        // FileStream(path) acquires its own Unix sharing flock. Open the descriptor directly so
        // contention belongs to the explicit Midge flock protocol and remains cancellable.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
        {
            throw new PantsNotSupportedException("Simulated cloud requires native conditional mutation locks on this platform.");
        }

        if (!File.Exists(path))
        {
            try
            {
                using var created = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.ReadWrite, 1, FileOptions.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process created this permanent stripe first.
            }
        }
        var closeOnExec = OperatingSystem.IsLinux() ? 0x80000
            : OperatingSystem.IsMacOS() ? 0x1000000 : 0x100000;
        var encodedPath = Marshal.StringToCoTaskMemUTF8(path);
        int descriptor;
        try
        {
            descriptor = Open(encodedPath, 2 | closeOnExec);
        }
        finally
        {
            Marshal.FreeCoTaskMem(encodedPath);
        }
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Could not open simulated mutation stripe: {new Win32Exception(error).Message}", error);
        }

        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            return new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    static extern int Open(nint path, int flags);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    static extern int Flock(int descriptor, int operation);
}
