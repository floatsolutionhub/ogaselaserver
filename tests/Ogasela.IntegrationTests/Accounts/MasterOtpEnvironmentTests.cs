using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ogasela.Application.Accounts;

namespace Ogasela.IntegrationTests.Accounts;

/// <summary>The master OTP (Default_Global_OTP) is honoured in every environment, Production included.</summary>
public class MasterOtpEnvironmentTests : IClassFixture<AccountsApiFactory>
{
    private const string MasterCode = "246810";
    private readonly AccountsApiFactory _factory;

    public MasterOtpEnvironmentTests(AccountsApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void MasterCode_IsActiveInEveryEnvironment(string environment)
    {
        using var app = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Otp:MasterCode", MasterCode);

            // Production skips appsettings.Development.json (and CI has no .env), so supply a
            // throwaway signing key or Program.cs refuses to start.
            builder.UseSetting("Jwt:Secret", "test-only-signing-key-not-used-anywhere-real-0123456789");
        });

        var settings = app.Services.GetRequiredService<IOptions<OtpSettings>>().Value;

        settings.MasterCode.Should().Be(MasterCode);
    }
}
