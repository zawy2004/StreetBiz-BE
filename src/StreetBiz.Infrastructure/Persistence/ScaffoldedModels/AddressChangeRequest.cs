using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class AddressChangeRequest
{
    public long address_change_id { get; set; }

    public long registration_id { get; set; }

    public string new_address { get; set; } = null!;

    public decimal? new_latitude { get; set; }

    public decimal? new_longitude { get; set; }

    public long? released_contract_id { get; set; }

    public long? requested_new_slot_id { get; set; }

    public string change_status { get; set; } = null!;

    public string? conflict_resolution_note { get; set; }

    public long? reviewed_by { get; set; }

    public string? reviewer_role { get; set; }

    public DateTime? reviewed_at { get; set; }

    public DateTime created_at { get; set; }

    public virtual UserAccount? UserAccount { get; set; }

    public virtual BusinessRegistration registration { get; set; } = null!;

    public virtual RentalContract? released_contract { get; set; }

    public virtual SidewalkSlot? requested_new_slot { get; set; }
}
