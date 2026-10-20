using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Order
{
    public long order_id { get; set; }

    public string order_code { get; set; } = null!;

    public long customer_user_id { get; set; }

    public long storefront_id { get; set; }

    public string? storefront_address_snapshot { get; set; }

    public string order_status { get; set; } = null!;

    public decimal subtotal_amount { get; set; }

    public decimal total_amount { get; set; }

    public string? rejection_reason { get; set; }

    public DateTime? placed_at { get; set; }

    public DateTime? completed_at { get; set; }

    public DateTime created_at { get; set; }

    public virtual ICollection<Complaint> Complaints { get; set; } = new List<Complaint>();

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    public virtual ICollection<OrderStatusHistory> OrderStatusHistories { get; set; } = new List<OrderStatusHistory>();

    public virtual ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();

    public virtual ICollection<RefundTransaction> RefundTransactions { get; set; } = new List<RefundTransaction>();

    public virtual Review? Review { get; set; }

    public virtual UserAccount customer_user { get; set; } = null!;

    public virtual Storefront storefront { get; set; } = null!;
}
