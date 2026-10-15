using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Api.Contracts.Accounts;
using Ogasela.Application.Accounts.GetCurrentUser;
using Ogasela.Application.Accounts.ProfileChanges;
using Ogasela.Application.Accounts.SetPushToken;
using Ogasela.Application.Accounts.ProfilePhoto;
using Ogasela.Application.Accounts.UpdateProfile;
using Ogasela.Application.Accounts.UserDocuments;
using Ogasela.Application.Accounts.VerificationOverview;

namespace Ogasela.Api.Controllers.Accounts;

[ApiController]
[Authorize]
[Route("api/v1/me")]
public sealed class MeController : ControllerBase
{
    private readonly ISender _sender;

    public MeController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Returns the authenticated user's own profile (User.Id, role, contact details, and business name if a Seller).</summary>
    [HttpGet]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetCurrentUserQuery(), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Updates the display name. Phone, email and business info changes need approval - see change-requests below.</summary>
    [HttpPatch("profile")]
    public async Task<IActionResult> UpdateProfile(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new UpdateProfileCommand(request.Name), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>The caller's phone/email/business-info change requests, newest first, with review status.</summary>
    [HttpGet("change-requests")]
    public async Task<IActionResult> GetChangeRequests(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetMyProfileChangesQuery(), cancellationToken)).ToActionResult(this);

    /// <summary>Sends a code to the new phone (SMS) or email. 409 if another account already uses it.</summary>
    [HttpPost("change-requests/contact/code")]
    public async Task<IActionResult> RequestContactChangeCode(RequestContactChangeCodeRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new RequestContactChangeCodeCommand(request.Type, request.Value), cancellationToken)).ToActionResult(this);

    /// <summary>Checks the code and files the phone/email change for staff approval - it isn't applied until approved.</summary>
    [HttpPost("change-requests/contact")]
    public async Task<IActionResult> SubmitContactChange(SubmitContactChangeRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new SubmitContactChangeCommand(request.Type, request.Value, request.Code), cancellationToken))
            .ToActionResult(this);

    /// <summary>Sellers: files a new business name/store address for staff approval.</summary>
    [HttpPost("change-requests/business")]
    public async Task<IActionResult> SubmitBusinessInfoChange(SubmitBusinessInfoChangeRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(new SubmitBusinessInfoChangeCommand(request.BusinessName, request.StoreAddress), cancellationToken))
            .ToActionResult(this);

    /// <summary>Withdraws a change that's still awaiting review.</summary>
    [HttpDelete("change-requests/{id:guid}")]
    public async Task<IActionResult> CancelChangeRequest(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new CancelProfileChangeCommand(id), cancellationToken)).ToActionResult(this);

    /// <summary>Registers (or clears, if Token is null) this device's push token, called on login/logout. The token is the native FCM/APNs device token, not an Expo push token - see PushNotificationChannel.</summary>
    [HttpPut("push-token")]
    public async Task<IActionResult> SetPushToken(SetPushTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new SetPushTokenCommand(request.Token), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>multipart/form-data with "image": sets the profile photo. Rejected (400) unless the photo shows exactly one clear face.</summary>
    [HttpPost("photo")]
    public async Task<IActionResult> UploadPhoto([FromForm(Name = "image")] IFormFile? image, CancellationToken cancellationToken)
    {
        if (image is null)
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]> { ["image"] = ["A photo is required."] }));
        }

        await using var stream = image.OpenReadStream();
        var result = await _sender.Send(new UploadProfilePhotoCommand(stream, image.FileName, image.ContentType), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>Personal and (sellers) business verification: status plus what's still missing, for the app's prompts.</summary>
    [HttpGet("verification")]
    public async Task<IActionResult> GetVerificationOverview(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetVerificationOverviewQuery(), cancellationToken)).ToActionResult(this);

    /// <summary>The caller's uploaded documents (ID card, utility bill), newest first, with review status.</summary>
    [HttpGet("documents")]
    public async Task<IActionResult> GetDocuments(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetMyDocumentsQuery(), cancellationToken)).ToActionResult(this);

    /// <summary>multipart/form-data: type (IdCard|UtilityBill, or BusinessRegistration|OfficeUtilityBill for sellers), idType + idNumber for an ID card, and file (image or PDF, up to 10MB).</summary>
    [HttpPost("documents")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadDocument([FromForm] UploadUserDocumentRequest request, CancellationToken cancellationToken)
    {
        if (request.File is null)
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]> { ["file"] = ["Choose a file to upload."] }));
        }

        await using var stream = request.File.OpenReadStream();
        var command = new UploadUserDocumentCommand(
            request.Type, request.IdType, request.IdNumber, stream, request.File.FileName, request.File.ContentType);
        return (await _sender.Send(command, cancellationToken)).ToActionResult(this);
    }
}
