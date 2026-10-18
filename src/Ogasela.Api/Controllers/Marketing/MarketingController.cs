using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ogasela.Api.Common;
using Ogasela.Api.Contracts.Marketing;
using Ogasela.Api.RateLimiting;
using Ogasela.Application.Marketing.RequestAccountDeletion;
using Ogasela.Application.Marketing.SubmitContactForm;
using Ogasela.Application.Marketing.SubmitInterestSignup;
using Ogasela.Domain.Marketing;

namespace Ogasela.Api.Controllers.Marketing;

/// <summary>
/// Public, anonymous endpoints for the ogasela.com marketing website: contact form, early-tester
/// and waiting-list signups, and account/data deletion requests. Every action requires a valid Cloudflare Turnstile
/// token (verified server-side) and sits behind the "marketing" rate-limit policy, since these are
/// the only anonymous write endpoints reachable from a page with no login at all.
/// </summary>
[ApiController]
[Route("api/v1/marketing")]
[EnableRateLimiting(RateLimitingExtensions.MarketingPolicy)]
public sealed class MarketingController : ControllerBase
{
    private readonly ISender _sender;

    public MarketingController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Submits a Contact Us message.</summary>
    [HttpPost("contact")]
    public async Task<IActionResult> SubmitContactForm(SubmitContactFormRequest request, CancellationToken cancellationToken)
    {
        var command = new SubmitContactFormCommand(
            request.Name, request.Email, request.Subject, request.Message, request.TurnstileToken);
        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>"Join our testers". Signing up twice with the same email is treated as success.</summary>
    [HttpPost("testers")]
    public Task<IActionResult> SubmitTesterSignup(SubmitInterestSignupRequest request, CancellationToken cancellationToken) =>
        SubmitInterestSignup(InterestKind.Tester, request, cancellationToken);

    /// <summary>"Join waiting list" - shown instead of "Get the app" until the app is in the stores. Signing up twice is treated as success.</summary>
    [HttpPost("waitlist")]
    public Task<IActionResult> SubmitWaitlistSignup(SubmitInterestSignupRequest request, CancellationToken cancellationToken) =>
        SubmitInterestSignup(InterestKind.Waitlist, request, cancellationToken);

    /// <summary>Submits an account/data deletion request. Anonymous by design - a user requesting deletion may not have (or want to use) their login.</summary>
    [HttpPost("deletion-requests")]
    public async Task<IActionResult> RequestAccountDeletion(RequestAccountDeletionRequest request, CancellationToken cancellationToken)
    {
        var command = new RequestAccountDeletionCommand(
            request.Email, request.Phone, request.RequestType, request.Details, request.TurnstileToken);
        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }

    private async Task<IActionResult> SubmitInterestSignup(
        InterestKind kind, SubmitInterestSignupRequest request, CancellationToken cancellationToken)
    {
        var command = new SubmitInterestSignupCommand(
            kind, request.Name, request.Email, request.Phone, request.State, request.Intent, request.DevicePlatform,
            request.Consent, request.TurnstileToken);
        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }
}
