using System.Security.Cryptography;
using System.Text;

namespace AzureBackup.Core.Crypto;

/// <summary>
/// Password-check token (`config.pwCheck`): a fixed marker sealed under the master key.
/// A wrong password derives a different master key, so opening the token fails —
/// letting both backup and restore reject a wrong password before doing any work.
/// </summary>
public static class PasswordVerifier
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("AzureBackup/pwcheck/v1");

    public static byte[] CreateToken(ReadOnlySpan<byte> masterKey) => Aead.Seal(masterKey, Marker);

    public static bool Verify(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> token)
    {
        try
        {
            byte[] opened = Aead.Open(masterKey, token);
            return CryptographicOperations.FixedTimeEquals(opened, Marker);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
