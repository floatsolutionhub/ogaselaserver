using MediatR;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.GetBlockedUsers;

public sealed record GetBlockedUsersQuery : IRequest<Result<IReadOnlyList<BlockedUserResponse>>>;

/// <summary>DisplayName is the business name for sellers, otherwise the user's own name (null if they never set one).</summary>
public sealed record BlockedUserResponse(Guid UserId, string? DisplayName, DateTime BlockedAt);
