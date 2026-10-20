using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Application.Marketing.Testimonials;

namespace Ogasela.Api.Controllers.Marketing;

public sealed record CreateTestimonialRequest(
    string Quote, string Name, string Role, int Rating, bool IsPublished, bool ConsentConfirmed);

public sealed record UpdateTestimonialRequest(string Quote, string Name, string Role, int Rating);

/// <summary>
/// Homepage testimonials: read publicly by the website, managed in the Control Portal's
/// Testimonials page with marketing.testimonials.manage. SuperAdmins pass every check.
/// </summary>
[ApiController]
public sealed class TestimonialsController : ControllerBase
{
    private const string ManagePolicy = "perm:marketing.testimonials.manage";

    private readonly ISender _sender;

    public TestimonialsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Public - published testimonials, newest first.</summary>
    [HttpGet("api/v1/testimonials")]
    public async Task<IActionResult> GetPublished(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetPublishedTestimonialsQuery(), cancellationToken)).ToActionResult(this);

    [HttpGet("api/v1/admin/testimonials")]
    [Authorize(Policy = ManagePolicy)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) =>
        (await _sender.Send(new GetAllTestimonialsQuery(), cancellationToken)).ToActionResult(this);

    [HttpPost("api/v1/admin/testimonials")]
    [Authorize(Policy = ManagePolicy)]
    public async Task<IActionResult> Create(CreateTestimonialRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(
            new CreateTestimonialCommand(
                request.Quote, request.Name, request.Role, request.Rating, request.IsPublished, request.ConsentConfirmed),
            cancellationToken)).ToActionResult(this);

    [HttpPut("api/v1/admin/testimonials/{id:guid}")]
    [Authorize(Policy = ManagePolicy)]
    public async Task<IActionResult> Update(Guid id, UpdateTestimonialRequest request, CancellationToken cancellationToken) =>
        (await _sender.Send(
            new UpdateTestimonialCommand(id, request.Quote, request.Name, request.Role, request.Rating),
            cancellationToken)).ToActionResult(this);

    [HttpPost("api/v1/admin/testimonials/{id:guid}/publish")]
    [Authorize(Policy = ManagePolicy)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new SetTestimonialPublishedCommand(id, true), cancellationToken)).ToActionResult(this);

    [HttpPost("api/v1/admin/testimonials/{id:guid}/unpublish")]
    [Authorize(Policy = ManagePolicy)]
    public async Task<IActionResult> Unpublish(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new SetTestimonialPublishedCommand(id, false), cancellationToken)).ToActionResult(this);

    [HttpDelete("api/v1/admin/testimonials/{id:guid}")]
    [Authorize(Policy = ManagePolicy)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await _sender.Send(new DeleteTestimonialCommand(id), cancellationToken)).ToActionResult(this);
}
