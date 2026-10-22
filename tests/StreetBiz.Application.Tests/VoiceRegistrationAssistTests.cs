using FluentAssertions;
using MediatR;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.AdministrativeUnits;
using StreetBiz.Application.DTOs.Sidewalk;
using StreetBiz.Application.Features.AdministrativeUnits.ListWards;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.SidewalkSlots.SearchSlots;
using StreetBiz.Application.Features.VendorRegistration.AssistTurn;

namespace StreetBiz.Application.Tests;

/// <summary>X01 "Kể chuyện là xong": the deterministic parts of the voice registration interview.</summary>
public sealed class VietnameseSpokenValuesTests
{
    // Thursday.
    private static readonly DateOnly Today = new(2026, 10, 22);

    [Theory]
    [InlineData("Vốn bỏ ra chừng ba chục triệu", 30_000_000)]
    [InlineData("vốn bỏ ra chừng 30 triệu", 30_000_000)]
    [InlineData("Chục triệu", 10_000_000)]
    [InlineData("Vốn ít lắm, khoảng năm triệu rưỡi thôi", 5_500_000)]
    [InlineData("khoảng 5 triệu rưỡi thôi", 5_500_000)]
    [InlineData("Vốn ít lắm, khoảng 5.5 triệu thôi", 5_500_000)]
    [InlineData("5,5 triệu", 5_500_000)]
    [InlineData("Tầm 15 triệu", 15_000_000)]
    [InlineData("Chắc khoảng một trăm hai mươi triệu, tính luôn cái xe", 120_000_000)]
    [InlineData("hai mươi lăm triệu", 25_000_000)]
    [InlineData("một tỷ rưỡi", 1_500_000_000)]
    [InlineData("500k", 500_000)]
    [InlineData("năm trăm ngàn", 500_000)]
    [InlineData("30 củ", 30_000_000)]
    [InlineData("30.000.000 đồng", 30_000_000)]
    public void Reads_spoken_amounts(string said, long expected) =>
        VietnameseSpokenValues.ParseMoney(said).Should().Be(expected);

    [Theory]
    [InlineData("Không phải ba trăm, ba mươi triệu thôi con")]
    [InlineData("Không phải 300, 30 triệu thôi con")]
    public void A_self_correction_keeps_the_last_amount(string said) =>
        VietnameseSpokenValues.ParseMoney(said).Should().Be(30_000_000);

    [Theory]
    [InlineData("vốn ba chục")] // no scale word: ambiguous, ask again
    [InlineData("Dạ để tui nghĩ coi, cái này tui không nhớ rõ")]
    [InlineData("")]
    public void Does_not_guess_an_amount(string said) =>
        VietnameseSpokenValues.ParseMoney(said).Should().BeNull();

    [Theory]
    [InlineData("Đầu tháng sau tui bắt đầu bán", "2026-11-01")]
    [InlineData("Thứ hai tuần sau là bán được rồi", "2026-10-26")]
    [InlineData("Thứ 2 tuần sau là bán được rồi", "2026-10-26")]
    [InlineData("Thứ Bảy tuần này là bán", "2026-10-24")]
    [InlineData("thứ sáu", "2026-10-23")]
    [InlineData("Mai bán luôn", "2026-10-23")]
    [InlineData("hôm nay", "2026-10-22")]
    [InlineData("Ngày 15 tháng 11", "2026-11-15")]
    [InlineData("Ngày mười lăm tháng mười một", "2026-11-15")]
    [InlineData("ngày hai mươi lăm tháng mười một", "2026-11-25")]
    [InlineData("Đầu tháng mười hai tui bắt đầu", "2026-12-01")]
    [InlineData("mùng 5 tháng 1", "2027-01-05")]
    [InlineData("15/11", "2026-11-15")]
    [InlineData("ngày 15 tháng 11 năm 2026", "2026-11-15")]
    [InlineData("giữa tháng sau", "2026-11-15")]
    public void Reads_spoken_start_dates(string said, string expected) =>
        VietnameseSpokenValues.ParseStartDate(said, Today).Should().Be(DateOnly.Parse(expected));

    [Theory]
    [InlineData("Chắc sau tết")]
    [InlineData("tuần sau")]
    [InlineData("Sau rằm tháng mười")]
    [InlineData("sắp tới")]
    public void Does_not_guess_a_vague_date(string said) =>
        VietnameseSpokenValues.ParseStartDate(said, Today).Should().BeNull();

    [Theory]
    [InlineData("Xe của tôi rộng chừng 1m2", 1.2)]
    [InlineData("Xe của tui rộng chừng một mét hai", 1.2)]
    [InlineData("rộng 1 mét 2", 1.2)]
    [InlineData("chừng 1,2 mét", 1.2)]
    [InlineData("Xe rộng chừng một mét rưỡi, dài hai mét", 1.5)]
    [InlineData("rộng chừng mét rưỡi", 1.5)]
    [InlineData("Cái gánh nhỏ thôi, chừng tám tấc", 0.8)]
    [InlineData("80 phân", 0.8)]
    public void Reads_cart_widths(string said, double expected) =>
        VietnameseSpokenValues.ParseMeters(said).Should().Be((decimal)expected);

    [Fact]
    public void Reads_values_back_in_words()
    {
        VietnameseSpokenValues.ReadMoney(30_000_000).Should().Be("Ba mươi triệu đồng");
        VietnameseSpokenValues.ReadPeople(3).Should().Be("Ba người");
        VietnameseSpokenValues.ReadDate(new DateOnly(2026, 10, 26)).Should().Be("Thứ Hai, ngày 26 tháng 10 năm 2026");
        VietnameseSpokenValues.ReadMeters(1.2m).Should().Be("1,2 mét");
    }
}

public sealed class InterviewPlannerTests
{
    private static readonly IReadOnlyList<WardDto> Wards =
    [
        new(10, "Phường Hải Châu 1", "Quận Hải Châu"),
        new(11, "Phường Thanh Khê Đông", "Quận Thanh Khê"),
    ];

    private static AssistDraft Complete(AssistDraft? draft = null) => (draft ?? new AssistDraft()) with
    {
        BusinessLine = "Bún bò",
        VendorType = VendorTypes.Itinerant,
        Latitude = 16.07m,
        Longitude = 108.21m,
        LocationConfirmed = true,
        WardUnitId = 10,
        CapitalAmount = 30_000_000,
        LaborCount = 2,
        PlannedStartDate = new DateOnly(2026, 11, 1),
        ContactSameAsPermanent = true,
        CartWidthSkipped = true,
        Confirmed = [AssistFields.CapitalAmount, AssistFields.LaborCount, AssistFields.PlannedStartDate],
    };

    [Fact]
    public void Starts_with_one_open_question_that_can_fill_several_fields()
    {
        var q = InterviewPlanner.Next(new AssistDraft(Salutation: "cô"), [], Wards);
        q!.Key.Should().Be(AssistQuestionKeys.Opening);
        q.Text.Should().StartWith("Cô bán món gì");
        q.Input.Should().Be(AssistInputs.Voice);
    }

    [Fact]
    public void Skips_what_the_opening_answer_already_gave()
    {
        var draft = new AssistDraft(BusinessLine: "Bún bò", VendorType: VendorTypes.Itinerant, Attempts: new Dictionary<string, int> { ["OPENING"] = 1 });
        InterviewPlanner.Next(draft, [], Wards)!.Key.Should().Be(AssistQuestionKeys.Location);
    }

    [Fact]
    public void Asks_for_the_dish_specifically_when_the_opening_gave_nothing()
    {
        var draft = new AssistDraft(Attempts: new Dictionary<string, int> { ["OPENING"] = 1 });
        InterviewPlanner.Next(draft, [], Wards)!.Key.Should().Be(AssistQuestionKeys.BusinessLine);
    }

    [Fact]
    public void Vendor_type_is_two_big_buttons()
    {
        var q = InterviewPlanner.Next(new AssistDraft(BusinessLine: "Bún bò"), [], Wards)!;
        q.Key.Should().Be(AssistQuestionKeys.VendorType);
        q.Input.Should().Be(AssistInputs.Choice);
        q.Choices.Select(c => c.Value).Should().BeEquivalentTo(VendorTypes.Itinerant, VendorTypes.FixedStorefront);
    }

    [Fact]
    public void A_found_spot_is_confirmed_on_the_map_not_by_voice()
    {
        var draft = new AssistDraft(BusinessLine: "Bún bò", VendorType: VendorTypes.Itinerant, LocationText: "chợ Cồn", Latitude: 16.07m, Longitude: 108.21m);
        var q = InterviewPlanner.Next(draft, [], Wards)!;
        q.Key.Should().Be(AssistQuestionKeys.Location);
        q.Input.Should().Be(AssistInputs.Map);
    }

    [Fact]
    public void Ward_buttons_put_the_best_guess_first_and_fall_back_to_every_ward()
    {
        var draft = Complete() with { WardUnitId = null };
        var guessed = InterviewPlanner.Next(draft, [new AssistWardOption(11, "Phường Thanh Khê Đông", AssistWardReasons.SlotsNearby)], Wards)!;
        guessed.Choices.Select(c => c.Value).Should().Equal("11");

        var all = InterviewPlanner.Next(draft, [], Wards)!;
        all.Choices.Select(c => c.Value).Should().Equal("10", "11");
    }

    [Fact]
    public void Only_a_fixed_storefront_is_asked_for_its_address()
    {
        InterviewPlanner.Next(Complete() with { VendorType = VendorTypes.FixedStorefront }, [], Wards)!.Key.Should().Be(AssistQuestionKeys.Address);
        InterviewPlanner.Next(Complete(), [], Wards).Should().BeNull();
    }

    [Fact]
    public void An_amount_is_read_back_and_must_be_confirmed_by_a_tap()
    {
        var draft = Complete() with { Confirmed = [AssistFields.LaborCount, AssistFields.PlannedStartDate] };
        var q = InterviewPlanner.Next(draft, [], Wards)!;
        q.Key.Should().Be("CONFIRM:" + AssistFields.CapitalAmount);
        q.Input.Should().Be(AssistInputs.Confirm);
        q.Text.Should().Be("Ba mươi triệu đồng, đúng không ạ?");
    }

    [Fact]
    public void Two_misses_switch_that_question_to_typing()
    {
        var draft = Complete() with { CapitalAmount = null, Attempts = new Dictionary<string, int> { [AssistQuestionKeys.Capital] = 2 } };
        var q = InterviewPlanner.Next(draft, [], Wards)!;
        q.Key.Should().Be(AssistQuestionKeys.Capital);
        q.Input.Should().Be(AssistInputs.Manual);
    }

    [Fact]
    public void Cart_width_is_optional_and_asked_once()
    {
        var draft = Complete() with { CartWidthSkipped = false };
        InterviewPlanner.Next(draft, [], Wards)!.Key.Should().Be(AssistQuestionKeys.CartWidth);
        InterviewPlanner.Next(draft with { Attempts = new Dictionary<string, int> { [AssistQuestionKeys.CartWidth] = 1 } }, [], Wards)
            .Should().BeNull();
    }

    [Fact]
    public void An_unknown_salutation_falls_back_to_ban()
    {
        InterviewPlanner.Next(new AssistDraft(Salutation: "mày"), [], Wards)!.Text.Should().StartWith("Bạn bán món gì");
    }
}

public sealed class AssistGuardsTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);

    private static ExtractedField F(string field, string? value, string evidence, decimal confidence = 0.9m) =>
        new(field, value, confidence, evidence);

    [Fact]
    public void A_value_whose_evidence_was_never_said_is_dropped()
    {
        var warnings = new List<string>();
        var accepted = AssistGuards.Accept(AssistQuestionKeys.Opening, "Tôi bán bún bò ngoài chợ cồm á",
            [F(AssistFields.BusinessLine, "Bún bò", "bán bún bò"), F(AssistFields.DeclaredAddress, "12 Lê Duẩn", "số 12 Lê Duẩn")],
            Today, warnings);

        accepted.Select(a => a.Field).Should().Equal(AssistFields.BusinessLine);
        warnings.Should().ContainSingle(w => w.Contains(AssistFields.DeclaredAddress));
    }

    [Fact]
    public void Evidence_matches_without_accents_case_or_punctuation()
    {
        AssistGuards.SaidIt("Tôi bán bún bò, ngoài chợ Cồn!", "ngoai cho con").Should().BeTrue();
        AssistGuards.SaidIt("Tôi bán bún bò", "bánh xèo").Should().BeFalse();
        AssistGuards.SaidIt("Tôi bán bún bò", "").Should().BeFalse();
    }

    [Fact]
    public void The_parser_reading_of_an_amount_beats_the_models_number()
    {
        var accepted = AssistGuards.Accept(AssistQuestionKeys.Capital, "Không phải 300, 30 triệu thôi con",
            [F(AssistFields.CapitalAmount, "300000000", "Không phải 300, 30 triệu thôi con")], Today, []);

        var capital = accepted.Single(a => a.Field == AssistFields.CapitalAmount);
        capital.Value.Should().Be("30000000");
        capital.Source.Should().Be(AssistProposalSources.Parser);
        capital.NeedsConfirmation.Should().BeTrue();
        capital.ReadBack.Should().Be("Ba mươi triệu đồng");
    }

    [Fact]
    public void An_amount_asked_for_is_read_even_when_the_model_misses_it()
    {
        var accepted = AssistGuards.Accept(AssistQuestionKeys.Capital, "Tầm 15 triệu", [], Today, []);
        accepted.Single().Value.Should().Be("15000000");
    }

    [Fact]
    public void A_start_date_in_the_past_or_beyond_a_year_is_refused()
    {
        var warnings = new List<string>();
        AssistGuards.Accept(AssistQuestionKeys.StartDate, "ngày 1 tháng 1 năm 2026", [], Today, warnings).Should().BeEmpty();
        warnings.Should().NotBeEmpty();
        AssistGuards.Accept(AssistQuestionKeys.StartDate, "abc", [F(AssistFields.PlannedStartDate, "2029-01-01", "abc")], Today, []).Should().BeEmpty();
    }

    [Fact]
    public void A_vague_date_from_the_model_is_not_turned_into_a_day_by_the_parser()
    {
        AssistGuards.Accept(AssistQuestionKeys.StartDate, "Chắc sau tết", [F(AssistFields.PlannedStartDate, null, "")], Today, [])
            .Should().BeEmpty();
    }

    [Fact]
    public void Vendor_type_must_be_a_known_code()
    {
        AssistGuards.Accept(AssistQuestionKeys.VendorType, "đẩy xe đi bán", [F(AssistFields.VendorType, "CART", "đẩy xe")], Today, [])
            .Should().BeEmpty();
        AssistGuards.Accept(AssistQuestionKeys.VendorType, "đẩy xe đi bán", [F(AssistFields.VendorType, VendorTypes.Itinerant, "đẩy xe")], Today, [])
            .Single().Value.Should().Be(VendorTypes.Itinerant);
    }

    [Fact]
    public void Low_confidence_text_is_not_accepted()
    {
        AssistGuards.Accept(AssistQuestionKeys.Opening, "bán bún bò", [F(AssistFields.BusinessLine, "Bún bò", "bún bò", 0.2m)], Today, [])
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("Dạ đúng rồi, tui ở đúng địa chỉ trên căn cước", "true")]
    [InlineData("Không, giờ tui ở trọ bên Thanh Khê rồi", "false")]
    public void Yes_or_no_is_read_even_without_the_model(string said, string expected) =>
        AssistGuards.Accept(AssistQuestionKeys.ContactAddress, said, [], Today, []).Single().Value.Should().Be(expected);

    [Fact]
    public void Contact_address_is_only_taken_when_it_was_the_question()
    {
        AssistGuards.Accept(AssistQuestionKeys.Opening, "đúng rồi", [F(AssistFields.ContactSameAsPermanent, "true", "đúng rồi")], Today, [])
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("Một mình tui thôi", "1")]
    [InlineData("3 người", "3")]
    public void A_head_count_asked_for_is_read_without_the_model(string said, string expected) =>
        AssistGuards.Accept(AssistQuestionKeys.Labor, said, [], Today, []).Single().Value.Should().Be(expected);

    [Fact]
    public void An_aside_never_overwrites_what_the_vendor_already_gave()
    {
        var draft = new AssistDraft(VendorType: VendorTypes.FixedStorefront, LocationText: "chợ Cồn", LocationConfirmed: true, Hours: "06:00-10:00");
        var proposals = new List<AssistFieldProposal>
        {
            new(AssistFields.DeclaredAddress, "45 Ông Ích Khiêm", 0.9m, "Số 45 đường Ông Ích Khiêm", null, false, AssistProposalSources.Ai),
            new(AssistFields.LocationText, "đường Ông Ích Khiêm", 0.9m, "đường Ông Ích Khiêm", null, false, AssistProposalSources.Ai),
            new(AssistFields.Hours, "07:00-11:00", 0.9m, "x", null, false, AssistProposalSources.Ai),
            new(AssistFields.CartWidthMeters, "1.2", 0.9m, "x", null, false, AssistProposalSources.Ai),
        };

        AssistGuards.KeepRelevant(proposals, AssistQuestionKeys.Address, draft).Select(p => p.Field)
            .Should().Equal(AssistFields.DeclaredAddress, AssistFields.CartWidthMeters);
        AssistGuards.KeepRelevant(proposals, AssistQuestionKeys.Opening, draft).Should().HaveCount(4);
    }

    [Fact]
    public void Hours_must_be_well_formed()
    {
        AssistGuards.TryParseHours("06:00-10:00", out var from, out var to).Should().BeTrue();
        from.Should().Be(new TimeOnly(6, 0));
        to.Should().Be(new TimeOnly(10, 0));
        AssistGuards.TryParseHours("18:00-", out _, out var open).Should().BeTrue();
        open.Should().BeNull();
        AssistGuards.TryParseHours("sáng", out _, out _).Should().BeFalse();
    }
}

public sealed class AssistRegistrationTurnHandlerTests
{
    private const long UserId = 7;
    private static readonly DateTime Now = new(2026, 10, 22, 3, 0, 0, DateTimeKind.Utc); // 10:00 in Đà Nẵng

    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<ICurrentUser> currentUser = new();
    private readonly Mock<IDateTimeProvider> clock = new();
    private readonly Mock<ISpeechToText> stt = new();
    private readonly Mock<IRegistrationExtractor> extractor = new();
    private readonly Mock<IGeocodingService> geocoding = new();
    private readonly Mock<ISender> sender = new();
    private readonly Mock<IAiAssistanceLogs> logs = new();

    private static readonly IReadOnlyList<WardDto> Wards =
    [
        new(10, "Phường Hải Châu 1", "Quận Hải Châu"),
        new(11, "Phường Thanh Khê Đông", "Quận Thanh Khê"),
    ];

    public AssistRegistrationTurnHandlerTests()
    {
        vendorContext.Setup(v => v.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(70);
        currentUser.SetupGet(c => c.UserId).Returns(UserId);
        clock.SetupGet(c => c.UtcNow).Returns(Now);
        sender.Setup(s => s.Send(It.IsAny<ListWardsQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Wards);
        sender.Setup(s => s.Send(It.IsAny<SearchSlotsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Slot(21, "LD-07", 10, 120), Slot(22, "LD-08", 10, 180), Slot(31, "TK-02", 11, 400)]);
    }

    private AssistRegistrationTurnCommandHandler Handler() => new(
        vendorContext.Object, currentUser.Object, clock.Object, stt.Object, extractor.Object, geocoding.Object, sender.Object, logs.Object);

    private static SidewalkSlotDto Slot(long id, string code, int ward, double distance) => new(
        id, code, 1, "Nguyễn Văn Linh", ward, 16.07m, 108.21m, 1.5m, 2m, "AVAILABLE", "WARD", 50_000,
        new TimeOnly(5, 0), new TimeOnly(11, 0), distance, [], false, false, false, null, null, null,
        "DAY", null, "STANDARD", null, 1_500_000, null);

    private static RegistrationExtraction Extracted(string transcript, params ExtractedField[] fields) =>
        new(transcript, fields, "test-model", "x01.v1", 10);

    [Fact]
    public async Task One_spoken_answer_fills_several_fields_and_finds_the_spot_and_nearby_slots()
    {
        const string said = "Tôi bán bún bò với bánh mì ngoài chợ cồm á, sáng 6 giờ tới 10 giờ";
        stt.Setup(s => s.TranscribeAsync(It.IsAny<byte[]>(), "audio/m4a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpeechTranscript(said, "groq:whisper", 400));
        extractor.Setup(e => e.ExtractAsync(It.Is<RegistrationExtractionInput>(i => i.Transcript == said), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Extracted(said,
                new ExtractedField(AssistFields.BusinessLine, "Bún bò, bánh mì", 0.95m, "bán bún bò với bánh mì"),
                new ExtractedField(AssistFields.LocationText, "chợ Cồn", 0.9m, "chợ cồm"),
                new ExtractedField(AssistFields.Hours, "06:00-10:00", 0.9m, "sáng 6 giờ tới 10 giờ")));
        geocoding.Setup(g => g.SearchAsync("chợ Cồn, Đà Nẵng", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GeoPoint(16.068m, 108.214m, "Chợ Cồn, Hải Châu, Đà Nẵng")]);

        var result = await Handler().Handle(
            new AssistRegistrationTurnCommand(AssistQuestionKeys.Opening, new AssistDraft(Salutation: "cô"), null, [1, 2, 3], "audio/m4a", true),
            CancellationToken.None);

        result.Transcript.Should().Be(said);
        result.Proposals.Select(p => p.Field).Should().BeEquivalentTo(AssistFields.BusinessLine, AssistFields.LocationText, AssistFields.Hours);
        result.Location!.Latitude.Should().Be(16.068m);
        result.Location.WardOptions.First().UnitId.Should().Be(10, "two of the three nearby slots belong to ward 10");
        result.SlotPreview.Select(s => s.SlotCode).Should().Equal("LD-07", "LD-08", "TK-02");
        result.SlotPreview[0].FitsHours.Should().BeTrue("06:00-10:00 is inside the slot's 05:00-11:00");
        result.Draft.BusinessLine.Should().Be("Bún bò, bánh mì");
        result.Draft.LocationConfirmed.Should().BeFalse("the vendor confirms the pin on the map");
        result.NextQuestion!.Key.Should().Be(AssistQuestionKeys.VendorType);
        result.IsAiGenerated.Should().BeTrue();
    }

    [Fact]
    public async Task Each_answered_turn_is_logged_with_a_hash_not_the_words()
    {
        const string said = "Vốn bỏ ra chừng ba chục triệu";
        extractor.Setup(e => e.ExtractAsync(It.IsAny<RegistrationExtractionInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Extracted(said));
        object? logged = null;
        logs.Setup(l => l.RecordAsync(AiFeatureCodes.VoiceRegistration, AiLogEntities.UserAccount, UserId, AssistQuestionKeys.Capital,
                It.IsAny<object>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, long, string?, object, decimal?, CancellationToken>((_, _, _, _, output, _, _) => logged = output)
            .ReturnsAsync(1);

        var result = await Handler().Handle(
            new AssistRegistrationTurnCommand(AssistQuestionKeys.Capital, Ready(), said, null, null, false), CancellationToken.None);

        result.Proposals.Single().Value.Should().Be("30000000");
        logged.Should().NotBeNull();
        System.Text.Json.JsonSerializer.Serialize(logged).Should().NotContain("ba chục").And.Contain("transcriptSha256");
    }

    [Fact]
    public async Task When_speech_to_text_is_down_the_extractor_hears_the_clip_itself()
    {
        stt.Setup(s => s.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiUnavailableException("down"));
        extractor.SetupGet(e => e.AcceptsAudio).Returns(true);
        extractor.Setup(e => e.ExtractAsync(It.Is<RegistrationExtractionInput>(i => i.Audio != null && i.Transcript == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Extracted("Có hai người bán, vợ chồng tui thôi",
                new ExtractedField(AssistFields.LaborCount, "2", 0.9m, "vợ chồng tui")));

        var result = await Handler().Handle(
            new AssistRegistrationTurnCommand(AssistQuestionKeys.Labor, Ready(), null, [1], "audio/webm;codecs=opus", true), CancellationToken.None);

        result.Proposals.Single().Value.Should().Be("2");
    }

    [Fact]
    public async Task With_no_working_provider_the_client_gets_a_503_to_fall_back_to_typing()
    {
        stt.Setup(s => s.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiUnavailableException("down"));
        extractor.SetupGet(e => e.AcceptsAudio).Returns(false);

        await FluentActions.Awaiting(() => Handler().Handle(
                new AssistRegistrationTurnCommand(AssistQuestionKeys.Labor, Ready(), null, [1], "audio/m4a", true), CancellationToken.None))
            .Should().ThrowAsync<AiUnavailableException>();
    }

    [Fact]
    public async Task A_planning_call_asks_the_next_question_without_calling_or_logging_AI()
    {
        var result = await Handler().Handle(
            new AssistRegistrationTurnCommand(AssistQuestionKeys.Opening, new AssistDraft(), null, null, null, false), CancellationToken.None);

        result.NextQuestion!.Key.Should().Be(AssistQuestionKeys.Opening);
        result.IsAiGenerated.Should().BeFalse();
        extractor.Verify(e => e.ExtractAsync(It.IsAny<RegistrationExtractionInput>(), It.IsAny<CancellationToken>()), Times.Never);
        logs.Verify(l => l.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string?>(),
            It.IsAny<object>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_answer_with_nothing_usable_counts_a_try_and_the_second_miss_switches_to_typing()
    {
        extractor.Setup(e => e.ExtractAsync(It.IsAny<RegistrationExtractionInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Extracted("để tui nghĩ coi"));

        var noCapital = Ready() with { CapitalAmount = null, Confirmed = [AssistFields.LaborCount, AssistFields.PlannedStartDate] };
        var first = await Handler().Handle(
            new AssistRegistrationTurnCommand(AssistQuestionKeys.Capital, noCapital, "để tui nghĩ coi", null, null, false), CancellationToken.None);
        first.Draft.Attempts![AssistQuestionKeys.Capital].Should().Be(1);
        first.NextQuestion!.Input.Should().Be(AssistInputs.Voice);

        var second = await Handler().Handle(
            new AssistRegistrationTurnCommand(AssistQuestionKeys.Capital, first.Draft, "để tui nghĩ coi", null, null, false), CancellationToken.None);
        second.NextQuestion!.Input.Should().Be(AssistInputs.Manual);
    }

    [Fact]
    public async Task A_place_the_map_cannot_find_is_reported_and_asked_again()
    {
        const string said = "Hay đứng ở chỗ không ai biết";
        extractor.Setup(e => e.ExtractAsync(It.IsAny<RegistrationExtractionInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Extracted(said, new ExtractedField(AssistFields.LocationText, "chỗ không ai biết", 0.8m, "chỗ không ai biết")));
        geocoding.Setup(g => g.SearchAsync(It.IsAny<string>(), 3, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await Handler().Handle(
            new AssistRegistrationTurnCommand(AssistQuestionKeys.Location, Ready() with { LocationConfirmed = false, Latitude = null, Longitude = null, WardUnitId = null },
                said, null, null, false), CancellationToken.None);

        result.Location.Should().BeNull();
        result.Warnings.Should().Contain(w => w.Contains("Chưa tìm thấy"));
        result.NextQuestion!.Key.Should().Be(AssistQuestionKeys.Location);
    }

    [Fact]
    public void Wards_with_nearby_slots_come_before_name_matches()
    {
        var options = AssistRegistrationTurnCommandHandler.WardOptions(
            [Slot(31, "TK-02", 11, 400)], "Chợ Cồn, Hải Châu, Đà Nẵng", Wards);

        options.Select(o => (o.UnitId, o.Reason)).Should().Equal((11, AssistWardReasons.SlotsNearby), (10, AssistWardReasons.NameMatch));
    }

    [Fact]
    public void A_changed_amount_loses_its_earlier_confirmation()
    {
        var draft = Ready();
        var merged = AssistRegistrationTurnCommandHandler.Merge(draft,
            [new AssistFieldProposal(AssistFields.CapitalAmount, "15000000", 1, "15 triệu", "Mười lăm triệu đồng", true, AssistProposalSources.Parser)],
            AssistQuestionKeys.Capital);

        merged.CapitalAmount.Should().Be(15_000_000);
        merged.Confirmed.Should().NotContain(AssistFields.CapitalAmount);
        merged.Confirmed.Should().Contain(AssistFields.LaborCount);
    }

    /// <summary>A draft past the location and ward steps, with the numbers already confirmed.</summary>
    private static AssistDraft Ready() => new(
        BusinessLine: "Bún bò",
        VendorType: VendorTypes.Itinerant,
        LocationText: "chợ Cồn",
        Latitude: 16.068m,
        Longitude: 108.214m,
        LocationConfirmed: true,
        WardUnitId: 10,
        CapitalAmount: 30_000_000,
        LaborCount: 2,
        PlannedStartDate: new DateOnly(2026, 11, 1),
        Confirmed: [AssistFields.CapitalAmount, AssistFields.LaborCount, AssistFields.PlannedStartDate],
        Salutation: "cô");
}

public sealed class AssistRegistrationTurnValidatorTests
{
    private static readonly AssistRegistrationTurnCommandValidator Validator = new();

    [Fact]
    public void A_clip_needs_its_own_voice_consent() =>
        Validator.Validate(new AssistRegistrationTurnCommand(AssistQuestionKeys.Opening, new AssistDraft(), null, [1], "audio/m4a", false))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Audio_and_text_together_are_refused() =>
        Validator.Validate(new AssistRegistrationTurnCommand(AssistQuestionKeys.Opening, new AssistDraft(), "bún bò", [1], "audio/m4a", true))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Unknown_questions_and_formats_are_refused()
    {
        Validator.Validate(new AssistRegistrationTurnCommand("DROP TABLE", new AssistDraft(), "x", null, null, false)).IsValid.Should().BeFalse();
        Validator.Validate(new AssistRegistrationTurnCommand(AssistQuestionKeys.Opening, new AssistDraft(), null, [1], "video/mp4", true)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_codec_parameter_does_not_break_the_format_check() =>
        Validator.Validate(new AssistRegistrationTurnCommand(AssistQuestionKeys.Opening, new AssistDraft(), null, [1], "audio/webm;codecs=opus", true))
            .IsValid.Should().BeTrue();

    [Fact]
    public void The_draft_cannot_carry_anything_read_from_the_CCCD()
    {
        // Names, numbers, birth dates and addresses from the card stay on the device (master prompt §3).
        var forbidden = new[] { "IdNumber", "FullName", "OwnerName", "DateOfBirth", "OwnerDateOfBirth", "PermanentAddress", "IdIssuedPlace", "IdIssuedDate", "Gender", "Ethnicity" };
        typeof(AssistDraft).GetProperties().Select(p => p.Name).Should().NotIntersectWith(forbidden);
    }
}

public sealed class ConfirmAssistedRegistrationHandlerTests
{
    private readonly Mock<IVendorContext> vendorContext = new();
    private readonly Mock<ICurrentUser> currentUser = new();
    private readonly Mock<IAiAssistanceLogs> logs = new();

    public ConfirmAssistedRegistrationHandlerTests()
    {
        currentUser.SetupGet(c => c.UserId).Returns(7);
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(700, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BizRegistration(700, 70, VendorTypes.Itinerant, "Bún bò", null, null, null, 10,
                RegistrationStatuses.Submitted, false, null, null, DateTime.UtcNow, null));
    }

    private ConfirmAssistedRegistrationCommandHandler Handler() => new(vendorContext.Object, currentUser.Object, logs.Object);

    private static ConfirmAssistedRegistrationCommand Command() =>
        new(700, new AssistConfirmationRequest(6, [AssistFields.BusinessLine], [AssistFields.CapitalAmount], 210));

    [Fact]
    public async Task Records_the_vendors_own_acceptance()
    {
        var at = new DateTime(2026, 10, 22, 3, 5, 0, DateTimeKind.Utc);
        logs.Setup(l => l.RecordAcceptedByAuthorAsync(AiFeatureCodes.VoiceRegistration, AiLogEntities.Registration, 700, 7,
                It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((55L, at));

        var result = await Handler().Handle(Command(), CancellationToken.None);

        result.Should().Be(new AssistConfirmationDto(55, 700, at));
    }

    [Fact]
    public async Task Confirming_twice_returns_the_first_record()
    {
        var at = new DateTime(2026, 10, 22, 3, 5, 0, DateTimeKind.Utc);
        logs.Setup(l => l.FindLatestAsync<object>(AiFeatureCodes.VoiceRegistration, AiLogEntities.Registration, 700, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiLogged<object>(55, null, new object(), at, true));

        (await Handler().Handle(Command(), CancellationToken.None)).AiLogId.Should().Be(55);
        logs.Verify(l => l.RecordAcceptedByAuthorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
            It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Someone_elses_registration_is_refused()
    {
        vendorContext.Setup(v => v.RequireOwnedRegistrationAsync(700, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("no"));

        await FluentActions.Awaiting(() => Handler().Handle(Command(), CancellationToken.None)).Should().ThrowAsync<NotFoundException>();
    }
}
