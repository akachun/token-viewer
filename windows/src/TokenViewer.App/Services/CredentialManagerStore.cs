using System.Runtime.InteropServices;
using System.Text;
using TokenViewer.Core.Services;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace TokenViewer.App.Services;

/// <summary>
/// Stores secrets as generic credentials in Windows Credential Manager (replaces macOS Keychain).
/// Entries appear as "TokenViewer:&lt;account&gt;" under Control Panel → Credential Manager.
/// </summary>
public sealed class CredentialManagerStore : ICredentialStore
{
    private const string Prefix = "TokenViewer:";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int MaxBlobBytes = 5 * 512;

    public void Save(string account, string secret)
    {
        var blob = Encoding.UTF8.GetBytes(secret);
        if (blob.Length > MaxBlobBytes)
            throw new InvalidOperationException($"자격 증명이 너무 깁니다 ({blob.Length} bytes > {MaxBlobBytes}).");

        var ptr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = Prefix + account,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = ptr,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"CredWrite 실패 (Win32 오류 {Marshal.GetLastWin32Error()})");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public string? Load(string account)
    {
        if (!CredRead(Prefix + account, CredTypeGeneric, 0, out var credPtr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public void Delete(string account) => CredDelete(Prefix + account, CredTypeGeneric, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
