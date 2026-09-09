using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class PaymentCallbackEvent
{
    public long callback_event_id { get; set; }

    public string provider { get; set; } = null!;

    public string? provider_reference { get; set; }

    public long? transaction_id { get; set; }

    public string raw_payload { get; set; } = null!;

    public bool signature_valid { get; set; }

    public string processing_result { get; set; } = null!;

    public DateTime received_at { get; set; }

    public virtual PaymentTransaction? transaction { get; set; }
}
