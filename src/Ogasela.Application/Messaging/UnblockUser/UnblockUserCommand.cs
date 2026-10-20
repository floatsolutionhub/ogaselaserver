using MediatR;
using Ogasela.Shared;

namespace Ogasela.Application.Messaging.UnblockUser;

public sealed record UnblockUserCommand(Guid TargetUserId) : IRequest<Result>;
