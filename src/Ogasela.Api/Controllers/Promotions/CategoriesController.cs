using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Api.Contracts.Promotions;
using Ogasela.Application.Promotions.CreateCategory;
using Ogasela.Application.Promotions.GetCategories;
using Ogasela.Application.Promotions.UpdateCategory;

namespace Ogasela.Api.Controllers.Promotions;

/// <summary>Category browsing is public; creating or editing a category (including its optional cover image) is SuperAdmin-only.</summary>
[ApiController]
public sealed class CategoriesController : ControllerBase
{
    private readonly ISender _sender;

    public CategoriesController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Public - the full category tree, including inactive/non-free-eligible ones.</summary>
    [HttpGet("api/v1/categories")]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetCategoriesQuery(), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>
    /// multipart/form-data; Image is optional. A top-level category must be created with at least
    /// one subcategory (repeat the "subcategories" field per name) - they're saved together. To add
    /// one more subcategory to an existing category later, set ParentCategoryId to that top-level
    /// category and leave subcategories empty.
    /// </summary>
    [HttpPost("api/v1/admin/categories")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> CreateCategory([FromForm] CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        await using var imageStream = request.Image?.OpenReadStream();

        var command = new CreateCategoryCommand(
            request.Name,
            request.ParentCategoryId,
            request.IsFreeEligible,
            request.AttributeSchemaVersion,
            imageStream,
            request.Image?.FileName,
            request.Image?.ContentType,
            request.Subcategories,
            request.IsOfficePickupEligible);

        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>multipart/form-data; a new Image replaces the existing one, otherwise it's left unchanged.</summary>
    [HttpPut("api/v1/admin/categories/{id:guid}")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> UpdateCategory(
        Guid id, [FromForm] UpdateCategoryRequest request, CancellationToken cancellationToken)
    {
        await using var imageStream = request.Image?.OpenReadStream();

        var command = new UpdateCategoryCommand(
            id,
            request.Name,
            request.ParentCategoryId,
            request.IsFreeEligible,
            request.AttributeSchemaVersion,
            imageStream,
            request.Image?.FileName,
            request.Image?.ContentType,
            request.IsOfficePickupEligible);

        var result = await _sender.Send(command, cancellationToken);
        return result.ToActionResult(this);
    }
}
