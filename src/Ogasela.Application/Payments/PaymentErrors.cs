using Ogasela.Shared;

namespace Ogasela.Application.Payments;

public static class PaymentErrors
{
    public static readonly Error PlanNotFound = new(
        "Payment.PlanNotFound", "The promotion plan could not be found.");

    public static readonly Error PaymentRequired = new(
        "Payment.Required", "Payment is required to publish under a paid plan.");

    public static readonly Error InsufficientBalance = new(
        "Payment.InsufficientBalance", "Your wallet balance is insufficient for this plan. Fund your wallet and try again.");

    public static readonly Error SellerProfileNotFound = new(
        "Payment.SellerProfileNotFound", "No seller profile was found for the current user.");

    public static readonly Error InvalidAmount = new(
        "Payment.InvalidAmount", "The amount must be greater than zero.");

    public static readonly Error GatewayError = new(
        "Payment.GatewayError", "The payment gateway could not process this request.");

    public static readonly Error UnknownProvider = new(
        "Payment.UnknownProvider", "The payment provider named in this webhook is not recognized.");

    public static readonly Error InvalidWebhookSignature = new(
        "Payment.InvalidWebhookSignature", "The webhook signature could not be verified.");

    public static readonly Error WebhookReferenceMissing = new(
        "Payment.WebhookReferenceMissing", "The webhook payload did not include a transaction reference.");

    public static readonly Error TransactionNotFound = new(
        "Payment.TransactionNotFound", "The transaction could not be found.");

    public static readonly Error TransactionNotRefundable = new(
        "Payment.TransactionNotRefundable", "Only a successful transaction can be refunded.");

    public static readonly Error WalletNotFound = new(
        "Payment.WalletNotFound", "No wallet was found for this seller.");

    public static readonly Error RefundExceedsWalletBalance = new(
        "Payment.RefundExceedsWalletBalance", "The seller's wallet balance is lower than the amount being refunded (it has already been spent).");

    public static readonly Error RefundFailed = new(
        "Payment.RefundFailed", "The payment provider couldn't process the refund right now. Nothing was changed - please try again.");

    public static readonly Error RefundAlreadyInProgress = new(
        "Payment.RefundAlreadyInProgress", "This payment is already being refunded.");
}
