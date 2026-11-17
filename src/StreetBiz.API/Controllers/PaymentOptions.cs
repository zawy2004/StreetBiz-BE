using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.API.Controllers;

/// <summary>
/// The payment methods the web app may offer. The platform-wide wallets (MoMo, ZaloPay)
/// are offered where they are configured, or all simulated in Development; PAYOS is offered only
/// for a payee that has connected its own ACTIVE receiving account, and comes first so the
/// payment screen can preselect it. The payee is named so the payer sees who is being paid.
/// </summary>
internal static class PaymentOptions
{
    /// <param name="forOrder">
    /// True for a customer's order, false for a ward's rental fee. A wallet configured with
    /// <c>Payments:{Momo|ZaloPay}:OrdersOnly</c> is left out of a fee's options.
    /// </param>
    public static object Build(
        bool sandboxEnabled, IConfiguration configuration, PayeeSummary? payee = null, bool forOrder = true)
    {
        // The same fields the gateway needs before it will open a MoMo checkout (IsMomoConfigured),
        // so the screen never offers MoMo that the checkout would then refuse.
        var momoLive =
            !string.IsNullOrWhiteSpace(configuration["Payments:Momo:ApiBaseUrl"])
            && !string.IsNullOrWhiteSpace(configuration["Payments:Momo:PartnerCode"])
            && !string.IsNullOrWhiteSpace(configuration["Payments:Momo:AccessKey"])
            && !string.IsNullOrWhiteSpace(configuration["Payments:Momo:SecretKey"])
            && !string.IsNullOrWhiteSpace(configuration["Payments:Momo:ReturnUrl"])
            && !string.IsNullOrWhiteSpace(configuration["Payments:Momo:NotifyUrl"])
            && (forOrder || !configuration.GetValue<bool>("Payments:Momo:OrdersOnly"));
        var zaloLive =
            configuration.GetValue<bool>("Payments:ZaloPay:Enabled")
            && !string.IsNullOrWhiteSpace(configuration["Payments:ZaloPay:AppId"])
            && !string.IsNullOrWhiteSpace(configuration["Payments:ZaloPay:Key1"])
            && !string.IsNullOrWhiteSpace(configuration["Payments:ZaloPay:Key2"])
            && (forOrder || !configuration.GetValue<bool>("Payments:ZaloPay:OrdersOnly"));
        var payOsLive = configuration.GetValue("Payments:PayOs:Enabled", true) && payee is { Active: true };

        // The simulator is only ever offered in Development; a real gateway is offered wherever it is configured.
        var platform = sandboxEnabled
            ? new[] { "MOMO", "ZALOPAY" }
                .Where(p => forOrder || !configuration.GetValue<bool>($"Payments:{(p == "MOMO" ? "Momo" : "ZaloPay")}:OrdersOnly"))
                .ToArray()
            : new[] { momoLive ? "MOMO" : null, zaloLive ? "ZALOPAY" : null }
                .OfType<string>().ToArray();
        var providers = payOsLive ? ["PAYOS", .. platform] : platform;

        // Without the simulator, whatever is offered moves real money (a bank transfer is real money).
        var live = momoLive || zaloLive || payOsLive || !sandboxEnabled;
        // The wallet gateways run against their test environments; a payOS bank transfer does not.
        var wallets = platform.Select(p => p == "MOMO" ? "MoMo" : "ZaloPay").ToArray();
        return new
        {
            mode = providers.Length == 0 ? "UNAVAILABLE" : live ? "LIVE" : "SANDBOX",
            providers,
            message = providers.Length == 0
                ? "Thanh toán trực tuyến chưa sẵn sàng. Vui lòng thử lại sau."
                : !live
                    ? "Thanh toán thử nghiệm, không trừ tiền thật."
                    : (momoLive || zaloLive) && wallets.Length > 0 && !payOsLive
                        ? "Thanh toán qua cổng " + string.Join(" / ", wallets) + " (môi trường test, không trừ tiền thật)."
                        : "Chuyển khoản ngân hàng qua VietQR, hệ thống tự xác nhận khi tiền về.",
            payee = payOsLive
                ? new
                {
                    name = payee!.DisplayName,
                    bankName = payee.BankName,
                    accountNumberMasked = payee.AccountNumberMasked,
                    accountName = payee.AccountName,
                }
                : null,
        };
    }
}
