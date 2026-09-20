using FluentAssertions;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;

namespace StreetBiz.Infrastructure.Tests;

public sealed class SqlErrorTranslatorTests
{
    // SqlException/SqlError have no public constructor, so these tests drive the testable
    // Translate(number, message) overload directly with the text the two RentalContracts
    // triggers actually RAISERROR (see db/StreetBiz_SQL_Server.sql).

    [Fact]
    public void Overlap_trigger_message_becomes_a_conflict()
    {
        var result = SqlErrorTranslator.Translate(50000,
            "Slot already has an active contract overlapping this period.");

        result.Should().BeOfType<ConflictException>()
            .Which.Message.Should().Be(SideMessages.SlotAlreadyBooked);
    }

    [Fact]
    public void Debt_trigger_message_becomes_a_domain_rule_violation()
    {
        var result = SqlErrorTranslator.Translate(50000,
            "Cannot return a slot while fees are overdue or penalties unpaid.");

        result.Should().BeOfType<DomainRuleException>()
            .Which.Message.Should().Be(SideMessages.CannotReturnWithDebt);
    }

    [Fact]
    public void An_unrecognized_50000_message_is_not_translated()
    {
        // Guards against the two trigger checks becoming overly broad and swallowing an
        // unrelated RAISERROR that might be added to the database later.
        SqlErrorTranslator.Translate(50000, "Some other application-defined error.")
            .Should().BeNull();
    }

    [Fact]
    public void Renewal_filtered_unique_index_violation_becomes_a_conflict()
    {
        var result = SqlErrorTranslator.Translate(2601,
            "Cannot insert duplicate key row in object 'dbo.RenewalRequests' with unique index 'UQ_RenewalRequests_OpenPerContract'.");

        result.Should().BeOfType<ConflictException>()
            .Which.Message.Should().Be(SideMessages.RenewalAlreadyOpen);
    }

    [Fact]
    public void Address_change_filtered_unique_index_violation_becomes_a_conflict()
    {
        var result = SqlErrorTranslator.Translate(2601,
            "Cannot insert duplicate key row in object 'dbo.AddressChangeRequests' with unique index 'UQ_AddressChangeRequests_OpenPerRegistration'.");

        result.Should().BeOfType<ConflictException>()
            .Which.Message.Should().Be(SideMessages.AddressChangeAlreadyOpen);
    }

    [Fact]
    public void Permit_live_per_contract_violation_becomes_a_conflict()
    {
        var result = SqlErrorTranslator.Translate(2601,
            "Cannot insert duplicate key row in object 'dbo.DigitalPermits' with unique index 'UQ_DigitalPermits_LivePerContract'.");

        result.Should().BeOfType<ConflictException>();
    }

    [Fact]
    public void Qr_payload_collision_falls_back_to_the_table_name()
    {
        // The auto-generated constraint name for a plain inline UNIQUE column (qr_payload) is
        // an unstable hash, so this must be recognized by table name, not by index name.
        var result = SqlErrorTranslator.Translate(2627,
            "Violation of UNIQUE KEY constraint 'UQ__DigitalP__4A9101CF620A0D90'. Cannot insert duplicate key in object 'dbo.DigitalPermits'.");

        result.Should().BeOfType<ConflictException>();
    }

    [Fact]
    public void Slot_code_collision_falls_back_to_the_table_name()
    {
        var result = SqlErrorTranslator.Translate(2627,
            "Violation of UNIQUE KEY constraint 'UQ__Sidewalk__5D19A1A4F1795837'. Cannot insert duplicate key in object 'dbo.SidewalkSlots'.");

        result.Should().BeOfType<ConflictException>()
            .Which.Message.Should().Be(SideMessages.SlotCodeGenerationFailed);
    }

    [Fact]
    public void A_duplicate_slot_hold_becomes_a_held_by_another_conflict()
    {
        // SlotHolds' primary key on slot_id is what stops two vendors holding the same slot.
        var result = SqlErrorTranslator.Translate(2627,
            "Violation of PRIMARY KEY constraint 'PK__SlotHold__971A01BB0168B98B'. Cannot insert duplicate key in object 'dbo.SlotHolds'.");

        result.Should().BeOfType<ConflictException>()
            .Which.Message.Should().Be(SideMessages.SlotHeldByAnother);
    }

    [Fact]
    public void Foreign_key_violation_becomes_a_validation_error_not_a_500()
    {
        var result = SqlErrorTranslator.Translate(547,
            "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_RentalApplications_Slot\".");

        result.Should().BeOfType<ValidationAppException>();
    }

    [Fact]
    public void An_unmapped_error_number_is_not_translated()
    {
        SqlErrorTranslator.Translate(4060, "Cannot open database requested by the login.")
            .Should().BeNull();
    }
}
