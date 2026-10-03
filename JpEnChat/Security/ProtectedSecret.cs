using System;
using System.Security.Cryptography;
using System.Text;

namespace JpEnChat.Security;

/// <summary>
/// Thin wrapper over Windows DPAPI (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>)
/// used to keep API keys out of the plaintext Dalamud config JSON.
/// </summary>
/// <remarks>
/// The ciphertext is bound to the current Windows user account, so a copied config file is useless on another
/// machine/account; the user simply re-enters the key there. Never log plaintext or ciphertext.
/// </remarks>
internal static class ProtectedSecret
{
    /// <summary>Encrypts <paramref name="plaintext"/> for the current user and returns base64 ciphertext.</summary>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows (Dalamud always is, including under Wine).</exception>
    public static string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Decrypts base64 ciphertext produced by <see cref="Protect"/>. Returns <c>null</c> for null/empty input,
    /// malformed base64, ciphertext from another user/machine, or a non-Windows platform. Never throws.
    /// </summary>
    public static string? Unprotect(string? b64)
    {
        if (string.IsNullOrEmpty(b64) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var cipher = Convert.FromBase64String(b64);
            var bytes = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
