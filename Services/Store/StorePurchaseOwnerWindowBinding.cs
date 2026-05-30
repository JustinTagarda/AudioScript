using System.Threading;

namespace AudioScript.Services.Store;

public static class StorePurchaseOwnerWindowBinding
{
    private static readonly AsyncLocal<IntPtr?> CurrentHandle = new();

    public static IntPtr GetCurrentOrDefault()
    {
        return CurrentHandle.Value ?? IntPtr.Zero;
    }

    public static IDisposable BeginScope(IntPtr ownerWindowHandle)
    {
        IntPtr? previous = CurrentHandle.Value;
        CurrentHandle.Value = ownerWindowHandle == IntPtr.Zero ? null : ownerWindowHandle;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly IntPtr? _previous;
        private int _disposed;

        public RestoreScope(IntPtr? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CurrentHandle.Value = _previous;
        }
    }
}
