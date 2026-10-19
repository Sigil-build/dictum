using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Dictum.Core.Configuration;

namespace Dictum.Platform.Windows;

/// <summary>Secrets in Windows Credential Manager as generic credentials named "dictum:&lt;key&gt;", stored as UTF-8.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class CredentialManagerSecretStore : ISecretStore
{
    private const string Prefix = "dictum:";

    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        if (!Win32.CredRead(Prefix + key, Win32.CredTypeGeneric, 0, out var credential))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == Win32.ErrorNotFound
                ? Task.FromResult<string?>(null)
                : throw new Win32Exception(error, "Reading from Credential Manager failed");
        }

        try
        {
            return Task.FromResult<string?>(Encoding.UTF8.GetString(credential->CredentialBlob, (int)credential->CredentialBlobSize));
        }
        finally
        {
            Win32.CredFree(credential);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct)
    {
        var blob = Encoding.UTF8.GetBytes(value);
        fixed (char* target = Prefix + key)
        fixed (char* user = Environment.UserName)
        fixed (byte* data = blob)
        {
            var credential = new Win32.Credential
            {
                Type = Win32.CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = Win32.CredPersistLocalMachine,
                UserName = user,
            };

            if (!Win32.CredWrite(&credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Writing to Credential Manager failed");
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        if (!Win32.CredDelete(Prefix + key, Win32.CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != Win32.ErrorNotFound)
            {
                throw new Win32Exception(error, "Deleting from Credential Manager failed");
            }
        }

        return Task.CompletedTask;
    }
}
