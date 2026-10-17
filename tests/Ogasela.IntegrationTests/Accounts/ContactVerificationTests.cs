using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ogasela.Api.Contracts.Accounts;
using Ogasela.Application.Accounts;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Accounts.Login;
using Ogasela.Domain.Accounts;

namespace Ogasela.IntegrationTests.Accounts;

/// <summary>
/// Every sign-in must be proven through a verified email or phone: signing in with one that isn't
/// verified yet sends the code there alone (a verification step), and entering it verifies it.
/// </summary>
public class ContactVerificationTests : IClassFixture<AccountsApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string Password = "Sup3rSecret!";
    private readonly AccountsApiFactory _factory;

    public ContactVerificationTests(AccountsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SigningInWithAnUnverifiedEmail_VerifiesThatEmail_ThenLaterSignInsAreNormal()
    {
        var client = _factory.CreateClient();
        var (phone, email) = await RegisterAsync(client);

        var first = await LoginAsync(client, email: email);
        first.VerificationRequired.Should().BeTrue();
        first.Channel.Should().Be("Email");
        first.MaskedPhone.Should().BeEmpty("the code must only go to the address being verified");

        // The phone isn't verified, so its key holds no login code - only the email does.
        (await PeekAsync(phone)).Should().BeNull();
        var code = await PeekAsync(PasswordResetOtpKey.ForEmail(email));
        code.Should().NotBeNullOrEmpty();
        (await VerifyAsync(client, null, email, code!)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await LoginAsync(client, email: email);
        second.VerificationRequired.Should().BeFalse("the email was verified by the first sign-in");
        second.MaskedPhone.Should().BeEmpty("the phone still isn't verified, so it doesn't get login codes");
    }

    [Fact]
    public async Task OnceBothAreVerified_TheCodeGoesToBoth()
    {
        var client = _factory.CreateClient();
        var (phone, email) = await RegisterAsync(client);

        await LoginAsync(client, phone: phone);
        (await VerifyAsync(client, phone, null, (await PeekAsync(phone))!)).EnsureSuccessStatusCode();
        await LoginAsync(client, email: email);
        (await VerifyAsync(client, null, email, (await PeekAsync(PasswordResetOtpKey.ForEmail(email)))!)).EnsureSuccessStatusCode();

        var byPhone = await LoginAsync(client, phone: phone);
        byPhone.VerificationRequired.Should().BeFalse();
        byPhone.MaskedEmail.Should().NotBeEmpty();
        byPhone.MaskedPhone.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AWrongCode_DoesNotVerifyOrSignIn()
    {
        var client = _factory.CreateClient();
        var (_, email) = await RegisterAsync(client);

        await LoginAsync(client, email: email);
        (await VerifyAsync(client, null, email, "000000")).StatusCode.Should().NotBe(HttpStatusCode.OK);

        (await LoginAsync(client, email: email)).VerificationRequired.Should().BeTrue();
    }

    private async Task<(string Phone, string Email)> RegisterAsync(HttpClient client)
    {
        var phone = $"080{Random.Shared.Next(0, 100_000_000):D8}";
        var email = $"verify-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(
            Phone: phone, Email: email, Password: Password, AccountType: UserRole.Buyer, BusinessName: null,
            RcNumber: null, Nin: null));
        response.EnsureSuccessStatusCode();
        return (phone, email);
    }

    private static async Task<LoginOtpSentResponse> LoginAsync(HttpClient client, string? phone = null, string? email = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(phone, email, Password));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LoginOtpSentResponse>(JsonOptions))!;
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string? phone, string? email, string code) =>
        client.PostAsJsonAsync("/api/v1/auth/login/verify", new VerifyLoginOtpRequest(phone, email, code));

    private async Task<string?> PeekAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOtpService>().PeekAsync(key, CancellationToken.None);
    }
}
