using MediatR;
using Ogasela.Shared;

namespace Ogasela.Application.Accounts.DeleteMyAccount;

/// <summary>A buyer or seller deleting their own account from the app. Immediate and irreversible.</summary>
public sealed record DeleteMyAccountCommand : IRequest<Result>;
