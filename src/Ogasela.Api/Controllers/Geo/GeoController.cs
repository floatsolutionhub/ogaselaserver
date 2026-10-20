using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Api.Contracts.Geo;
using Ogasela.Application.Geo.CreateCity;
using Ogasela.Application.Geo.CreateState;
using Ogasela.Application.Geo.DeleteCity;
using Ogasela.Application.Geo.DeleteState;
using Ogasela.Application.Geo.GetCitiesByState;
using Ogasela.Application.Geo.GetCity;
using Ogasela.Application.Geo.GetNearestCity;
using Ogasela.Application.Geo.GetState;
using Ogasela.Application.Geo.GetStates;
using Ogasela.Application.Geo.UpdateCity;
using Ogasela.Application.Geo.UpdateState;

namespace Ogasela.Api.Controllers.Geo;

/// <summary>
/// States/cities are public reference data - the GET routes are reachable by any caller,
/// authenticated or not. Only the write routes are permission-gated.
/// </summary>
[ApiController]
[Route("api/v1/geo")]
public sealed class GeoController : ControllerBase
{
    private readonly ISender _sender;

    public GeoController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet("states")]
    public async Task<IActionResult> GetStates(CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetStatesQuery(), cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("states/{stateId:guid}")]
    public async Task<IActionResult> GetState(Guid stateId, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetStateQuery(stateId), cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("states/{stateId:guid}/cities")]
    public async Task<IActionResult> GetCitiesByState(Guid stateId, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetCitiesByStateQuery(stateId), cancellationToken);
        return result.ToActionResult(this);
    }

    /// <summary>
    /// The seeded city closest to a device's position - fills in state and city from "use my location".
    /// <c>state</c> is the state the device says it's in, when it knows (narrows the search to it).
    /// </summary>
    [HttpGet("cities/nearest")]
    public async Task<IActionResult> GetNearestCity(
        [FromQuery] decimal latitude, [FromQuery] decimal longitude, [FromQuery] string? state, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetNearestCityQuery(latitude, longitude, state), cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpGet("cities/{cityId:guid}")]
    public async Task<IActionResult> GetCity(Guid cityId, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetCityQuery(cityId), cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPost("states")]
    [Authorize(Policy = "perm:geo.state.manage")]
    public async Task<IActionResult> CreateState(CreateStateRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new CreateStateCommand(request.Name, request.Code), cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPut("states/{id:guid}")]
    [Authorize(Policy = "perm:geo.state.manage")]
    public async Task<IActionResult> UpdateState(Guid id, UpdateStateRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new UpdateStateCommand(id, request.Name, request.Code), cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpDelete("states/{id:guid}")]
    [Authorize(Policy = "perm:geo.state.manage")]
    public async Task<IActionResult> DeleteState(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new DeleteStateCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPost("cities")]
    [Authorize(Policy = "perm:geo.city.manage")]
    public async Task<IActionResult> CreateCity(CreateCityRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new CreateCityCommand(request.StateId, request.Name, request.Latitude, request.Longitude), cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpPut("cities/{id:guid}")]
    [Authorize(Policy = "perm:geo.city.manage")]
    public async Task<IActionResult> UpdateCity(Guid id, UpdateCityRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new UpdateCityCommand(id, request.Name, request.Latitude, request.Longitude), cancellationToken);
        return result.ToActionResult(this);
    }

    [HttpDelete("cities/{id:guid}")]
    [Authorize(Policy = "perm:geo.city.manage")]
    public async Task<IActionResult> DeleteCity(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new DeleteCityCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }
}
