using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Application.DTOs.Payments;

/// <summary>
/// The VietQR and the details the payer needs for a bank transfer (SePay or payOS).
/// <c>TransferContent</c> must be sent unchanged: it carries the code that matches the money to its
/// payment. SePay gives a QR image (<c>QrImageUrl</c>); payOS gives the QR content to draw
/// (<c>QrContent</c>) and its own hosted payment page (<c>CheckoutUrl</c>).
/// </summary>
public sealed record BankTransferDto(
    string BankName,
    string AccountNumber,
    string? AccountName,
    decimal Amount,
    string TransferContent,
    string QrImageUrl,
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
                instructions.QrImageUrl,
                instructions.QrContent,
                instructions.CheckoutUrl);
}
