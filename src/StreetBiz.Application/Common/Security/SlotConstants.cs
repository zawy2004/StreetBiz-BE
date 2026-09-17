namespace StreetBiz.Application.Common.Security;

/// <summary>SidewalkSlots.slot_status values (DB CHECK).</summary>
public static class SlotStatuses
{
    public const string Available = "AVAILABLE";
    public const string PendingApplication = "PENDING_APPLICATION";
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
}

/// <summary>SidewalkSlots.source values (DB CHECK).</summary>
public static class SlotSources
{
    public const string WardDefined = "WARD_DEFINED";
    public const string VendorProposed = "VENDOR_PROPOSED";
}

/// <summary>SidewalkSlots.proposal_review_status values (DB CHECK, SIDE-11/WARD-16).</summary>
public static class ProposalReviewStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

/// <summary>RentalApplications.application_method values (DB CHECK).</summary>
public static class ApplicationMethods
{
    public const string AutoAdjacent = "AUTO_ADJACENT";
    public const string ManualSelected = "MANUAL_SELECTED";
}

/// <summary>RentalApplications.application_status values (DB CHECK).</summary>
public static class ApplicationStatuses
{
    public const string Pending = "PENDING";
    public const string UnderReview = "UNDER_REVIEW";
    public const string MoreInformationRequired = "MORE_INFORMATION_REQUIRED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";

    /// <summary>Statuses a vendor may still withdraw (SIDE-04).</summary>
    public static readonly string[] Withdrawable = [Pending, UnderReview, MoreInformationRequired];

    /// <summary>Statuses that count as an open, in-flight application for a slot (same set as <see cref="Withdrawable"/>).</summary>
    public static readonly string[] Open = Withdrawable;
}

/// <summary>RentalContracts.contract_status values (DB CHECK).</summary>
public static class ContractStatuses
{
    public const string Active = "ACTIVE";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
    public const string Suspended = "SUSPENDED";
    public const string Revoked = "REVOKED";
}

/// <summary>RenewalRequests.renewal_status values (DB CHECK).</summary>
public static class RenewalStatuses
{
    public const string Pending = "PENDING";
    public const string UnderReview = "UNDER_REVIEW";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";

    /// <summary>Statuses that count as an open, in-flight renewal (UQ_RenewalRequests_OpenPerContract).</summary>
    public static readonly string[] Open = [Pending, UnderReview];
}

/// <summary>AddressChangeRequests.change_status values (DB CHECK).</summary>
public static class AddressChangeStatuses
{
    public const string Pending = "PENDING";
    public const string UnderReview = "UNDER_REVIEW";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";

    /// <summary>Statuses that count as open (UQ_AddressChangeRequests_OpenPerRegistration).</summary>
    public static readonly string[] Open = [Pending, UnderReview];
}

/// <summary>SlotTransferRequests.transfer_status values (DB CHECK).</summary>
public static class TransferStatuses
{
    public const string Pending = "PENDING";
    public const string AcceptedByReceiver = "ACCEPTED_BY_RECEIVER";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";

    /// <summary>Statuses that count as an open transfer for a contract.</summary>
    public static readonly string[] Open = [Pending, AcceptedByReceiver];
}

/// <summary>Direction filter for listing a vendor's slot transfer requests (SIDE-12/13).</summary>
public static class TransferDirections
{
    public const string Outgoing = "outgoing";
    public const string Incoming = "incoming";
    public static readonly string[] All = [Outgoing, Incoming];
}

/// <summary>DigitalPermits.permit_status values (DB CHECK).</summary>
public static class PermitStatuses
{
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
    public const string Revoked = "REVOKED";
}

/// <summary>vw_PermitValidity.effective_status computed values (SIDE-08, WARD-11, BUY-02).</summary>
public static class PermitEffectiveStatuses
{
    public const string Valid = "VALID";
    public const string NotYetValid = "NOT_YET_VALID";
    public const string Suspended = "SUSPENDED";
    public const string Expired = "EXPIRED";
    public const string Revoked = "REVOKED";
}

/// <summary>FeeScheduleItems.item_status / Penalties.penalty_status values relevant to SIDE-07's debt check.</summary>
public static class DebtStatuses
{
    public const string FeeItemOverdue = "OVERDUE";
    public const string PenaltyUnpaid = "UNPAID";
}

/// <summary>Messages for the Sidewalk Slot & Rental workflow (SIDE).</summary>
public static class SideMessages
{
    public const string SlotNotFound = "Sidewalk slot not found.";
    public const string SlotNotAvailable = "This slot is not available for rent.";
    public const string SlotAlreadyBooked = "This slot already has an active contract for the requested period.";
    public const string ApplicationSubmitted = "Your rental application has been submitted successfully.";
    public const string ApplicationNotFound = "Rental application not found.";
    public const string ApplicationAlreadyOpenForSlot = "This slot already has an application under review.";
    public const string ApplicationNotWithdrawable = "This application can no longer be withdrawn because it has already been decided.";
    public const string ApplicationWithdrawn = "Your rental application has been withdrawn.";
    public const string NotEligibleForAdjacent = "Only fixed-storefront vendors may apply for a storefront-adjacent slot."; // BR pre-check
    public const string RegistrationMissingAddress = "This registration has no address on file to measure adjacency from.";
    public const string OutsideAdjacentRadius = "The selected slot is too far from the registered business address."; // BR-11
    public const string AlreadyHasActiveAdjacentContract = "This registration already has an active storefront-adjacent contract."; // BR-12
    public const string ContractNotFound = "Rental contract not found.";
    public const string CannotReturnWithDebt = "Cannot return a slot while fees are overdue or penalties unpaid."; // mirrors TR_RentalContracts_NoCancelWithDebt
    public const string RenewalAlreadyOpen = "A renewal request is already pending for this contract.";
    public const string RenewalRequested = "Your renewal request has been submitted successfully.";
    public const string ContractNotActive = "This action requires an active rental contract.";
    public const string ContractCancelled = "Your rental slot has been returned successfully.";
    public const string PermitNotFound = "No digital permit has been issued for this contract yet.";
    public const string ProposalPhotoRequired = "A photo of the proposed location is required."; // WARD-16 evidence
    public const string ZoneNotFound = "Please select a valid pricing zone.";
    public const string SlotCodeGenerationFailed = "Could not generate a unique slot code. Please try again.";
    public const string SlotProposed = "Your proposed slot has been submitted for review.";
    public const string AddressChangeNotEligible = "Only fixed-storefront registrations may request an address change.";
    public const string AddressChangeAlreadyOpen = "An address change request is already pending for this registration.";
    public const string AddressChangeNotFound = "Address change request not found.";
    public const string AddressChangeRequested = "Your address change request has been submitted successfully.";
    public const string ReleasedContractNotOwned = "The slot to release does not belong to this registration.";
    public const string TransferSameVendor = "Cannot transfer a slot to yourself.";
    public const string TransferAlreadyOpen = "A transfer request is already pending for this contract.";
    public const string ReceiverNotFound = "No vendor account was found for that phone number.";
    public const string ReceiverNotApproved = "The receiving vendor has no approved business registration."; // BR-26
    public const string TransferBlockedByDebt = "Cannot transfer a slot while fees are overdue or penalties unpaid."; // BR-27
    public const string TransferNotFound = "Slot transfer request not found.";
    public const string TransferNotPending = "This transfer request is no longer pending.";
    public const string NotTheReceivingVendor = "Only the receiving vendor may accept or decline this transfer.";
    public const string TransferRequested = "Your slot transfer request has been submitted successfully.";
    public const string TransferAccepted = "You have accepted the slot transfer request.";
    public const string TransferDeclined = "You have declined the slot transfer request.";
}
