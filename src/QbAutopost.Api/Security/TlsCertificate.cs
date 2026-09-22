using System.Security.Cryptography.X509Certificates;
using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Security;

/// <summary>
/// FR-A-14: the server certificate, from a PFX file or from the machine store by thumbprint. Every failure here stops
/// startup with the reason — a server that silently fell back to no certificate would be the exact failure TLS is
/// configured to prevent — and the password is never part of any message (CLAUDE.md rule 6).
/// </summary>
public static class TlsCertificate
{
    public static X509Certificate2 Load(TlsSettings tls)
    {
        if (!string.IsNullOrWhiteSpace(tls.PfxPath))
        {
            var path = Path.GetFullPath(tls.PfxPath);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Api:Tls:PfxPath names {path}, which does not exist.");
            }

            try
            {
                return new X509Certificate2(path, tls.PfxPassword);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The certificate at {path} could not be opened. Check Api:Tls:PfxPassword, which must come from "
                    + "the QBAUTOPOST__Api__Tls__PfxPassword environment variable.", ex);
            }
        }

        return FromStore(tls.StoreThumbprint);
    }

    private static X509Certificate2 FromStore(string thumbprint)
    {
        // Spaces and the invisible left-to-right mark are what you get when a thumbprint is copied out of the
        // certificate dialog, and an operator should not have to know that.
        var wanted = new string(thumbprint.Where(char.IsLetterOrDigit).ToArray());
        if (wanted.Length == 0)
        {
            throw new InvalidOperationException("Api:Tls:StoreThumbprint is blank; name a certificate or set Api:Tls:PfxPath.");
        }

        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.FirstOrDefault(
                c => string.Equals(c.Thumbprint, wanted, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return found;
            }
        }

        throw new InvalidOperationException(
            $"No certificate with thumbprint {wanted} was found in the machine or user store. "
            + "Check that it is installed for the account this service runs as, with its private key.");
    }
}
