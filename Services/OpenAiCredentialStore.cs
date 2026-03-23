using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioScript.Services;

public sealed class OpenAiCredentialStore
{
    private const string DefaultCredentialTarget = "AudioScript.OpenAI.ApiKey";

    private readonly string _credentialTarget;
    private readonly ICredentialStore _credentialStore;

    public OpenAiCredentialStore(string? credentialTarget = null)
    {
        _credentialTarget = string.IsNullOrWhiteSpace(credentialTarget)
            ? DefaultCredentialTarget
            : credentialTarget.Trim();
        _credentialStore = new WindowsCredentialStore();
    }

    internal OpenAiCredentialStore(string? credentialTarget, ICredentialStore credentialStore)
    {
        _credentialTarget = string.IsNullOrWhiteSpace(credentialTarget)
            ? DefaultCredentialTarget
            : credentialTarget.Trim();
        _credentialStore = credentialStore;
    }

    public OpenAiCredentialSnapshot Load()
    {
        try
        {
            if (_credentialStore.TryRead(_credentialTarget, out string apiKey) && !string.IsNullOrWhiteSpace(apiKey))
            {
                return new OpenAiCredentialSnapshot(apiKey);
            }
        }
        catch
        {
            // Fall back to empty when reading fails.
        }

        return new OpenAiCredentialSnapshot(string.Empty);
    }

    public void Save(string apiKey)
    {
        string normalized = apiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            Clear();
            return;
        }

        try
        {
            _credentialStore.Write(_credentialTarget, normalized);
        }
        catch
        {
            // Keep settings UI responsive if persistence fails.
        }
    }

    public void Clear()
    {
        try
        {
            _credentialStore.Delete(_credentialTarget);
        }
        catch
        {
            // Keep settings UI responsive if cleanup fails.
        }
    }

    internal interface ICredentialStore
    {
        bool TryRead(string target, out string secret);
        bool Write(string target, string secret);
        bool Delete(string target);
    }

    private sealed class WindowsCredentialStore : ICredentialStore
    {
        private const int CredTypeGeneric = 1;
        private const int CredPersistLocalMachine = 2;

        public bool TryRead(string target, out string secret)
        {
            secret = string.Empty;

            if (!CredReadW(target, CredTypeGeneric, 0, out IntPtr credentialPtr))
            {
                return false;
            }

            try
            {
                var credential = Marshal.PtrToStructure<CREDENTIALW>(credentialPtr);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
                {
                    return false;
                }

                byte[] blob = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                secret = Encoding.UTF8.GetString(blob).Trim();
                Array.Clear(blob, 0, blob.Length);
                return !string.IsNullOrWhiteSpace(secret);
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }

        public bool Write(string target, string secret)
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
            IntPtr blobPtr = Marshal.AllocCoTaskMem(secretBytes.Length);

            try
            {
                Marshal.Copy(secretBytes, 0, blobPtr, secretBytes.Length);

                var credential = new CREDENTIALW
                {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    CredentialBlobSize = secretBytes.Length,
                    CredentialBlob = blobPtr,
                    Persist = CredPersistLocalMachine,
                    UserName = string.Empty,
                };

                bool success = CredWriteW(ref credential, 0);
                if (!success)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    throw new Win32Exception(errorCode, "Failed to write OpenAI API key to Windows Credential Manager.");
                }

                return true;
            }
            finally
            {
                Array.Clear(secretBytes, 0, secretBytes.Length);
                Marshal.FreeCoTaskMem(blobPtr);
            }
        }

        public bool Delete(string target)
        {
            if (CredDeleteW(target, CredTypeGeneric, 0))
            {
                return true;
            }

            int errorCode = Marshal.GetLastWin32Error();
            const int errorNotFound = 1168;
            if (errorCode == errorNotFound)
            {
                return false;
            }

            throw new Win32Exception(errorCode, "Failed to remove OpenAI API key from Windows Credential Manager.");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIALW
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredReadW(
            string target,
            int type,
            int flags,
            out IntPtr credentialPtr);

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWriteW(ref CREDENTIALW userCredential, int flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDeleteW(string target, int type, int flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
        private static extern void CredFree(IntPtr buffer);
    }
}

public sealed record OpenAiCredentialSnapshot(string ApiKey);
