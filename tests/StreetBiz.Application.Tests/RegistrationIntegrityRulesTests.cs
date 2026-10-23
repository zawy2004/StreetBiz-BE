using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.RegistrationIntegrity;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>AIC-03: what each finding is called, how serious it is, and what the officer may see of it.</summary>
public sealed class RegistrationIntegrityRulesTests
{
    private const int OfficerWard = 10;

    private static RelatedRegistrationFact Related(
        long id, string status = "APPROVED", int ward = OfficerWard, string? note = null) =>
        new(id, $"Hộ {id}", ward, ward == OfficerWard ? "Phường Hải Châu" : "Phường Thanh Khê", status, note);

    private static RegistrationIntegrityFacts Facts(
        string? idNumber = "048185000001",
        IReadOnlyList<RelatedRegistrationFact>? sameId = null,
        IReadOnlyList<RelatedRegistrationFact>? sameAddress = null,
        int burst = 1,
        int openApplications = 0,
        IReadOnlyList<RelatedRegistrationFact>? contested = null) =>
        new(1, idNumber, "12 Lê Duẩn", sameId ?? [], sameAddress ?? [], burst, openApplications, contested ?? []);

    [Fact]
    public void A_registration_with_nothing_unusual_has_no_flags()
    {
        var result = RegistrationIntegrityRules.Evaluate(Facts(), OfficerWard);

        result.Level.Should().Be(IntegritySeverities.None);
        result.Flags.Should().BeEmpty();
        result.HasIdNumber.Should().BeTrue();
    }

    [Fact]
    public void The_same_cccd_on_a_live_registration_of_another_account_is_high()
    {
        var result = RegistrationIntegrityRules.Evaluate(Facts(sameId: [Related(7)]), OfficerWard);

        result.Level.Should().Be(IntegritySeverities.High);
        var flag = result.Flags.Should().ContainSingle().Subject;
        flag.Code.Should().Be(IntegrityFlagCodes.IdSharedAcrossVendors);
        flag.Detail.Should().Contain("còn hiệu lực");
    }

    [Fact]
    public void The_same_cccd_only_on_rejected_or_withdrawn_filings_is_medium()
    {
        var result = RegistrationIntegrityRules.Evaluate(
            Facts(sameId: [Related(7, "REJECTED"), Related(8, "WITHDRAWN")]), OfficerWard);

        result.Level.Should().Be(IntegritySeverities.Medium);
        result.Flags.Single().Detail.Should().Contain("2 hồ sơ");
    }

    [Fact]
    public void A_shared_address_is_medium_and_asks_for_an_on_site_check()
    {
        var result = RegistrationIntegrityRules.Evaluate(Facts(sameAddress: [Related(9)]), OfficerWard);

        var flag = result.Flags.Should().ContainSingle().Subject;
        flag.Code.Should().Be(IntegrityFlagCodes.AddressSharedAcrossVendors);
        flag.Severity.Should().Be(IntegritySeverities.Medium);
        flag.Detail.Should().Contain("đối chiếu tại chỗ");
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void A_burst_of_filings_is_low_from_three_registrations(int count, bool flagged)
    {
        var result = RegistrationIntegrityRules.Evaluate(Facts(burst: count), OfficerWard);

        result.Flags.Any(f => f.Code == IntegrityFlagCodes.RegistrationBurst).Should().Be(flagged);
        if (flagged)
        {
            result.Level.Should().Be(IntegritySeverities.Low);
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Many_open_applications_is_flagged_from_three(int count, bool flagged)
    {
        var result = RegistrationIntegrityRules.Evaluate(Facts(openApplications: count), OfficerWard);

        result.Flags.Any(f => f.Code == IntegrityFlagCodes.ManyOpenApplications).Should().Be(flagged);
    }

    [Fact]
    public void Competing_with_oneself_for_a_slot_through_two_accounts_is_high_and_names_the_slot()
    {
        var result = RegistrationIntegrityRules.Evaluate(
            Facts(contested: [Related(7, note: "Ô NVL-03")]), OfficerWard);

        var flag = result.Flags.Should().ContainSingle().Subject;
        flag.Code.Should().Be(IntegrityFlagCodes.SlotContestedBySameId);
        flag.Severity.Should().Be(IntegritySeverities.High);
        flag.Related.Single().Note.Should().Be("Ô NVL-03");
    }

    [Fact]
    public void Flags_are_ordered_most_serious_first_and_the_level_is_the_worst()
    {
        var result = RegistrationIntegrityRules.Evaluate(
            Facts(burst: 4, sameAddress: [Related(9)], sameId: [Related(7)]), OfficerWard);

        result.Flags.Select(f => f.Severity).Should().Equal(
            IntegritySeverities.High, IntegritySeverities.Medium, IntegritySeverities.Low);
        result.Level.Should().Be(IntegritySeverities.High);
    }

    [Fact]
    public void A_household_in_another_ward_is_shown_only_by_ward_and_status()
    {
        var result = RegistrationIntegrityRules.Evaluate(
            Facts(sameId: [Related(7), Related(8, ward: 11)]), OfficerWard);

        var related = result.Flags.Single().Related;
        related[0].RegistrationId.Should().Be(7);
        related[0].BusinessName.Should().Be("Hộ 7");
        related[1].RegistrationId.Should().BeNull();
        related[1].BusinessName.Should().BeNull();
        related[1].WardName.Should().Be("Phường Thanh Khê");
    }

    [Fact]
    public void Without_an_id_number_the_result_says_so_rather_than_claiming_a_clean_check()
    {
        var result = RegistrationIntegrityRules.Evaluate(Facts(idNumber: null), OfficerWard);

        result.HasIdNumber.Should().BeFalse();
    }

    [Theory]
    [InlineData("12 Lê Duẩn, P. Hải Châu 1", "12 le duan p hai chau 1")]
    [InlineData("  45  Đường   Nguyễn-Văn-Linh ", "45 duong nguyen van linh")]
    [InlineData("Số 9/3 Trần Phú.", "so 9 3 tran phu")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Addresses_are_compared_without_case_accents_or_punctuation(string? address, string expected)
    {
        AddressKey.Normalize(address).Should().Be(expected);
    }

    [Fact]
    public async Task The_handler_answers_not_found_for_a_registration_outside_the_officer_s_ward()
    {
        var reader = new Mock<IRegistrationIntegrityReader>();
        reader.Setup(x => x.GetFactsAsync(10, 99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RegistrationIntegrityFacts?)null);
        var actor = new Mock<IWardActorContext>();
        actor.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new WardActor(2, 10, "Cán bộ"));

        var act = () => new GetRegistrationIntegrityQueryHandler(actor.Object, reader.Object)
            .Handle(new GetRegistrationIntegrityQuery(99), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
