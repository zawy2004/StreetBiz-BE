using System;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

/// <summary>The deposit handed back when a contract ended (db/StreetBiz_SQL_Server.sql: DepositRefunds).</summary>
public partial class DepositRefund
{
    public long refund_id { get; set; }

    public long contract_id { get; set; }

    public decimal deposit_paid { get; set; }

    public decimal amount { get; set; }

    public string? deduction_reason { get; set; }

    public string method { get; set; } = null!;

    public string? reference { get; set; }

    public long refunded_by { get; set; }

    public DateTime refunded_at { get; set; }

    public virtual RentalContract contract { get; set; } = null!;
}
