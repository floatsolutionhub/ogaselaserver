using FluentAssertions;
using Microsoft.Extensions.Options;
using Ogasela.Infrastructure.Payments;

namespace Ogasela.UnitTests.Payments;

public class PaymentReturnUrlPolicyTests
{
    private static PaymentReturnUrlPolicy Policy(params string[] allowed) =>
        new(Options.Create(new PaymentReturnUrlSettings { AllowedReturnUrls = allowed }));

    [Theory]
    [InlineData("https://market.ogasela.com/payments/return")]
    [InlineData("https://market.ogasela.com/payments/return/")]
    [InlineData("https://market.ogasela.com/payments/return?kind=listing&id=1")]
    [InlineData("https://MARKET.ogasela.com/payments/return/listing")]
    public void Allows_the_listed_page_and_below(string url)
    {
        Policy("https://market.ogasela.com/payments/return").IsAllowed(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.example/payments/return")]
    [InlineData("https://market.ogasela.com.evil.example/payments/return")]
    [InlineData("http://market.ogasela.com/payments/return")]
    [InlineData("https://market.ogasela.com:8443/payments/return")]
    [InlineData("https://market.ogasela.com/payments/returned")]
    [InlineData("https://market.ogasela.com/account")]
    [InlineData("https://market.ogasela.com/payments/return/../../account")]
    [InlineData("https://user:pass@market.ogasela.com/payments/return")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ogasela://payment-complete")]
    [InlineData("/payments/return")]
    [InlineData("")]
    public void Refuses_anything_else(string url)
    {
        Policy("https://market.ogasela.com/payments/return").IsAllowed(url).Should().BeFalse();
    }

    [Fact]
    public void Refuses_everything_when_nothing_is_configured()
    {
        Policy().IsAllowed("https://market.ogasela.com/payments/return").Should().BeFalse();
    }
}
