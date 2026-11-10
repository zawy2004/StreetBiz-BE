using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Application.DTOs.Payments;

/// <summary>
/// The VietQR and the details the payer needs for a bank transfer by payOS.
/// <c>TransferContent</c> must be sent unchanged: it carries the code that matches the money to its
/// payment. <c>QrContent</c> is the QR to draw, <c>CheckoutUrl</c> payOS's own hosted payment page.
/// </summary>
public sealed record BankTransferDto(
    string BankName,
    string AccountNumber,
    string? AccountName,
    decimal Amount,
    string TransferContent,
    string? QrContent = null,
    string? CheckoutUrl = null)
{
    public static BankTransferDto? From(BankTransferInstructions? instructions) =>
        instructions is null
            ? null
            : new BankTransferDto(
                instructions.BankName,
                instructions.AccountNumber,
                instructions.AccountName,
                instructions.Amount,
                instructions.TransferContent,
                instructions.QrContent,
                instructions.CheckoutUrl);
}
