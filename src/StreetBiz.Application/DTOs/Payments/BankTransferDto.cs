using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Application.DTOs.Payments;

/// <summary>
/// The VietQR and the details the payer needs for a SePay bank transfer. <c>TransferContent</c>
/// must be sent unchanged: it carries the code that matches the money to its payment.
/// </summary>
public sealed record BankTransferDto(
    string BankName,
    string AccountNumber,
    string? AccountName,
    decimal Amount,
    string TransferContent,
    string QrImageUrl)
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
                instructions.QrImageUrl);
}
