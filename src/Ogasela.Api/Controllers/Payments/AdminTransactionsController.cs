using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ogasela.Api.Common;
using Ogasela.Application.Payments.RefundTransaction;

namespace Ogasela.Api.Controllers.Payments;

/// <summary>Wallet operations support for FinanceAdmin and SuperAdmin (the Revenue page).</summary>
[ApiController]
[Authorize(Roles = "FinanceAdmin,SuperAdmin")]
public sealed class AdminTransactionsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminTransactionsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Reverses a successful Transaction. An ad-plan charge is refunded the way it was paid: card/transfer payments back through the payment gateway, older wallet payments back to the wallet. Only a Success transaction can be refunded.</summary>
    [HttpPost("api/v1/admin/transactions/{id:guid}/refund")]
    public async Task<IActionResult> Refund(Guid id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new RefundTransactionCommand(id), cancellationToken);
        return result.ToActionResult(this);
    }
}
