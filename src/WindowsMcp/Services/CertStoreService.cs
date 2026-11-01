using System.Security.Cryptography.X509Certificates;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class CertStoreService : ICertStoreService
{
    public Task<CertInfoDto[]> ListAsync(string location = "LocalMachine", string storeName = "Root", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!Enum.TryParse<StoreLocation>(location, ignoreCase: true, out var storeLocation))
            throw new ArgumentException($"Unknown store location '{location}'; expected LocalMachine or CurrentUser");

        var resolvedName = storeName.Equals("CA", StringComparison.OrdinalIgnoreCase)
            ? StoreName.CertificateAuthority
            : Enum.TryParse<StoreName>(storeName, ignoreCase: true, out var parsed)
                ? parsed
                : throw new ArgumentException(
                    $"Unknown store name '{storeName}'; expected Root|CA|My|AuthRoot|Disallowed|TrustedPeople|TrustedPublisher|AddressBook");

        var now = DateTime.Now;
        using var store = new X509Store(resolvedName, storeLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        var certs = store.Certificates
            .Cast<X509Certificate2>()
            .Select(c => new CertInfoDto(
                Subject: c.Subject,
                Issuer: c.Issuer,
                Thumbprint: c.Thumbprint,
                NotAfter: c.NotAfter,
                SelfSigned: string.Equals(c.Subject, c.Issuer, StringComparison.OrdinalIgnoreCase),
                Expired: c.NotAfter < now))
            .ToArray();

        return Task.FromResult(certs);
    }
}
