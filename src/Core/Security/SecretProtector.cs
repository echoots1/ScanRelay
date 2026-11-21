using Microsoft.AspNetCore.DataProtection;

namespace ScanRelay.Core.Security;

/// <summary>
/// Encrypts secrets before they are stored. On Windows the keys are themselves
/// protected for the machine, and the data folder's access rights are the second layer.
/// </summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("ScanRelay.Secrets.v1");

    public string Protect(string secret) => _protector.Protect(secret);

    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);

    public byte[] ProtectBytes(string secret) => _protector.Protect(System.Text.Encoding.UTF8.GetBytes(secret));

    public string UnprotectBytes(byte[] protectedSecret) => System.Text.Encoding.UTF8.GetString(_protector.Unprotect(protectedSecret));
}
