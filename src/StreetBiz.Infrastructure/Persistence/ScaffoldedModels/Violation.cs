using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Violation
{
    public long violation_id { get; set; }

    public long? contract_id { get; set; }

    public long? slot_id { get; set; }

    public long? vendor_id { get; set; }

    public string violation_type { get; set; } = null!;

    public string? description { get; set; }

    public string? evidence_url { get; set; }

    public long recorded_by { get; set; }

    public string? recorder_role { get; set; }

    public string source { get; set; } = null!;

    public long? source_report_id { get; set; }

    public DateTime recorded_at { get; set; }

    public string? bien_ban_so { get; set; }

    public string? prepared_location { get; set; }

    public string? witness_name { get; set; }

    public string? witness_role { get; set; }

    public string? witness_occupation { get; set; }

    public string? witness_address { get; set; }

    public string? violator_full_name { get; set; }

    public DateOnly? violator_date_of_birth { get; set; }

    public string? violator_gender { get; set; }

    public string? violator_nationality { get; set; }

    public string? violator_id_number { get; set; }

    public DateOnly? violator_id_issued_date { get; set; }

    public string? violator_id_issued_place { get; set; }

    public string? violator_address { get; set; }

    public string? containment_measures { get; set; }

    public bool explanation_required { get; set; }

    public string? explanation_method { get; set; }

    public DateTime? explanation_deadline_at { get; set; }

    public DateTime? explanation_received_at { get; set; }

    public string? explanation_content { get; set; }

    public DateTime? delivered_at { get; set; }

    public string? delivered_to_name { get; set; }

    public bool delivery_refused { get; set; }

    public string? delivery_refusal_reason { get; set; }

    public virtual Penalty? Penalty { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual RentalContract? contract { get; set; }

    public virtual SidewalkSlot? slot { get; set; }

    public virtual VendorReport? source_report { get; set; }

    public virtual Vendor? vendor { get; set; }

    public virtual ViolationType violation_typeNavigation { get; set; } = null!;
}
