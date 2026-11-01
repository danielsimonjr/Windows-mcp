using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class CertStoreServiceTests
{
    [Fact]
    public async Task ListAsync_returns_root_certificates_with_self_signed_cas()
    {
        var certs = await new CertStoreService().ListAsync("LocalMachine", "Root");

        certs.Should().NotBeEmpty();
        certs.Should().OnlyContain(c => !string.IsNullOrEmpty(c.Thumbprint));
        // The Root store is, by definition, full of self-signed trust anchors.
        certs.Should().Contain(c => c.SelfSigned);
    }

    [Fact]
    public async Task ListAsync_rejects_an_unknown_location()
    {
        var act = () => new CertStoreService().ListAsync("Nowhere", "Root");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*location*");
    }

    [Fact]
    public async Task ListAsync_rejects_an_unknown_store_name()
    {
        var act = () => new CertStoreService().ListAsync("LocalMachine", "NotARealStore");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*store*");
    }
}
