using FluentAssertions;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.WardConfiguration;

namespace StreetBiz.Application.Tests;

/// <summary>
/// AiInsightRules is the whole decision surface behind AIC-04/06/07 -- no database, no HTTP, so
/// every branch is covered here rather than through WardAiInsightsTests or AiComplianceServiceTests.
/// </summary>
public sealed class AiInsightRulesTests
{
    private static AiProposalSiteInput Input(IReadOnlyList<PlacementIssue> ruleChecks, decimal? widthMeters = 0m) =>
        new(SlotId: 1, Latitude: 16.06, Longitude: 108.22, WidthMeters: widthMeters, LengthMeters: 3m,
            ZoneName: "Đường Nguyễn Văn Linh", AvailableFrom: null, AvailableTo: null,
            ProposalPhotoUrl: null, BoundaryVerified: true, RuleChecks: ruleChecks);

    private static PlacementIssue Block(string code = "feature_blocks_business") =>
        new(PlacementSeverities.Block, code, "Trong hành lang kỹ thuật, cấm kinh doanh.", null, null, null);

    private static PlacementIssue Warn(string code = "slot_overlap") =>
        new(PlacementSeverities.Warn, code, "Gần một ô khác.", null, null, null);

    #region AIC-04: MergeProposal / FallbackProposalAssessment
    [Fact]
    public void Fallback_with_a_block_issue_is_likely_infeasible()
    {
        var result = AiInsightRules.FallbackProposalAssessment(Input([Block()]));

        result.IsAiGenerated.Should().BeFalse();
        result.Recommendation.Should().Be(ProposalRecommendations.LikelyInfeasible);
        result.ObstructionLevel.Should().Be(ObstructionLevels.Unknown);
        result.Reasons.Should().Contain(r => r.Contains("chưa xác minh bằng AI"));
    }

    [Fact]
    public void Fallback_with_only_a_warning_needs_survey_not_infeasible()
    {
        var result = AiInsightRules.FallbackProposalAssessment(Input([Warn()]));

        result.Recommendation.Should().Be(ProposalRecommendations.NeedsSurvey);
    }

    [Fact]
    public void A_block_issue_always_overrides_the_models_own_answer()
    {
        var result = AiInsightRules.MergeProposal(
            Input([Block()]), modelWidthMeters: 5m, modelObstructionLevel: "LOW", modelConfidence: 99,
            modelReasons: ["Vỉa hè rất rộng"], usedSatelliteImage: true, usedProposalPhoto: true);

        result.Recommendation.Should().Be(ProposalRecommendations.LikelyInfeasible,
            "a system rule block must win even if the model is highly confident the site is fine");
    }

    [Theory]
    [InlineData(1.0, 80, ProposalRecommendations.LikelyInfeasible)] // narrow + confident => infeasible
    [InlineData(1.0, 30, ProposalRecommendations.NeedsSurvey)] // narrow + unsure => survey, not a flat no
    [InlineData(3.0, 70, ProposalRecommendations.LikelyFeasible)] // wide + confident => feasible
    [InlineData(3.0, 50, ProposalRecommendations.NeedsSurvey)] // wide but not confident enough => survey
    public void Recommendation_follows_remaining_width_and_confidence(double modelWidth, int confidence, string expected)
    {
        var result = AiInsightRules.MergeProposal(
            Input([], widthMeters: 0m), (decimal)modelWidth, "MEDIUM", confidence, [], usedSatelliteImage: true, usedProposalPhoto: false);

        result.Recommendation.Should().Be(expected);
        result.RemainingPedestrianWidthMeters.Should().Be((decimal)modelWidth);
    }

    [Fact]
    public void Remaining_width_subtracts_the_slots_own_footprint()
    {
        var result = AiInsightRules.MergeProposal(
            Input([], widthMeters: 1.2m), modelWidthMeters: 2.5m, modelObstructionLevel: "LOW",
            modelConfidence: 90, modelReasons: [], usedSatelliteImage: true, usedProposalPhoto: false);

        result.RemainingPedestrianWidthMeters.Should().Be(1.3m);
    }

    [Fact]
    public void Null_model_width_leaves_remaining_width_null_and_forces_a_survey()
    {
        var result = AiInsightRules.MergeProposal(
            Input([]), modelWidthMeters: null, modelObstructionLevel: "LOW", modelConfidence: 95, modelReasons: [],
            usedSatelliteImage: true, usedProposalPhoto: false);

        result.RemainingPedestrianWidthMeters.Should().BeNull();
        result.Recommendation.Should().Be(ProposalRecommendations.NeedsSurvey,
            "high confidence about obstruction level is not the same as knowing there is 1.5m of clear path");
    }

    [Theory]
    [InlineData("low", ObstructionLevels.Low)]
    [InlineData("HIGH", ObstructionLevels.High)]
    [InlineData("banana", ObstructionLevels.Unknown)]
    [InlineData(null, ObstructionLevels.Unknown)]
    public void Obstruction_level_is_normalized_and_unknown_values_do_not_pass_through(string? modelValue, string expected)
    {
        var result = AiInsightRules.MergeProposal(
            Input([]), modelWidthMeters: 2m, modelObstructionLevel: modelValue, modelConfidence: 70,
            modelReasons: [], usedSatelliteImage: true, usedProposalPhoto: false);

        result.ObstructionLevel.Should().Be(expected);
    }

    [Fact]
    public void Confidence_is_clamped_to_0_100()
    {
        var result = AiInsightRules.MergeProposal(
            Input([]), 2m, "LOW", modelConfidence: 150, modelReasons: [], usedSatelliteImage: true, usedProposalPhoto: false);

        result.Confidence.Should().Be(100);
    }
    #endregion

    #region AIC-06: EvaluateDrift / CompassWord
    private static AiInsightRules.DriftScan Scan(long id, double lat, double lon, string context = "WARD_INSPECTION") =>
        new(id, DateTime.UtcNow, context, lat, lon);

    [Fact]
    public void No_scans_means_nothing_to_evaluate()
    {
        AiInsightRules.EvaluateDrift(16.06, 108.22, []).Should().BeNull();
    }

    [Fact]
    public void Every_scan_within_tolerance_is_not_flagged()
    {
        // ~5m away -- well inside the 25m tolerance.
        var evaluation = AiInsightRules.EvaluateDrift(0, 0, [Scan(1, 0.00004, 0)]);
        evaluation.Should().BeNull();
    }

    [Fact]
    public void A_single_offsite_scan_is_a_watch_not_a_drift()
    {
        // ~95m north of the anchor.
        var evaluation = AiInsightRules.EvaluateDrift(0, 0, [Scan(1, 0.00085, 0)]);

        evaluation.Should().NotBeNull();
        evaluation!.Level.Should().Be(DriftLevels.Watch);
        evaluation.OffSiteCount.Should().Be(1);
    }

    [Fact]
    public void Repeated_offsite_scans_in_the_same_direction_are_a_consistent_drift()
    {
        var scans = new[]
        {
            Scan(1, 0.0, 0.0), // on-site, within tolerance
            Scan(2, 0.00085, 0.00001), // ~95m north
            Scan(3, 0.00086, -0.00001), // ~96m north
        };

        var evaluation = AiInsightRules.EvaluateDrift(0, 0, scans);

        evaluation.Should().NotBeNull();
        evaluation!.ScanCount.Should().Be(3);
        evaluation.OffSiteCount.Should().Be(2);
        evaluation.Level.Should().Be(DriftLevels.Drift);
        evaluation.Pattern.Should().Be(DriftPatterns.ConsistentDirection);
        evaluation.MeanOffsetMeters.Should().BeGreaterThan(80).And.BeLessThan(110);
        evaluation.MeanOffsetBearingDegrees.Should().NotBeNull();
        AiInsightRules.CompassWord(evaluation.MeanOffsetBearingDegrees!.Value).Should().Be("Bắc");
    }

    [Fact]
    public void Offsite_scans_scattered_in_different_directions_are_not_a_consistent_drift()
    {
        var scans = new[]
        {
            Scan(1, 0.00027, 0.0), // ~30m north
            Scan(2, 0.0, 0.00028), // ~30m east (slightly different magnitude than north to avoid exact cancellation)
        };

        var evaluation = AiInsightRules.EvaluateDrift(0, 0, scans);

        evaluation.Should().NotBeNull();
        evaluation!.Level.Should().Be(DriftLevels.Drift);
        evaluation.Pattern.Should().Be(DriftPatterns.Scattered);
        evaluation.MeanOffsetMeters.Should().BeNull();
    }

    [Fact]
    public void Drift_explanation_names_the_direction_only_for_a_consistent_pattern()
    {
        var consistent = AiInsightRules.DriftExplanation(2, 3, DriftPatterns.ConsistentDirection, 95, 0);
        var scattered = AiInsightRules.DriftExplanation(2, 2, DriftPatterns.Scattered, null, null);

        consistent.Should().Contain("Bắc").And.Contain("chưa xác minh bằng AI");
        scattered.Should().NotContain("khoảng", "a scattered pattern has no single bearing/distance to quote").And.Contain("chưa xác minh bằng AI");
    }

    [Theory]
    [InlineData(0, "Bắc")]
    [InlineData(90, "Đông")]
    [InlineData(180, "Nam")]
    [InlineData(270, "Tây")]
    [InlineData(45, "Đông Bắc")]
    public void Compass_word_matches_the_bearing(double bearing, string expected)
    {
        AiInsightRules.CompassWord(bearing).Should().Be(expected);
    }
    #endregion

    #region AIC-07: PriceBaseline / rounding / clamping
    [Fact]
    public void No_slots_is_insufficient_data_and_keeps_the_current_price()
    {
        var result = AiInsightRules.PriceBaseline(currentPricePerDay: 25000, slotCount: 0, occupancyPercent: 0, pendingApplications: 0);

        result.Direction.Should().Be(PriceDirections.InsufficientData);
        result.BaselinePricePerDay.Should().Be(25000);
        result.MinAllowedPricePerDay.Should().Be(25000);
        result.MaxAllowedPricePerDay.Should().Be(25000);
    }

    [Fact]
    public void High_occupancy_raises_the_price_about_ten_percent_rounded_to_the_nearest_1000()
    {
        // The seed's HD zone scenario: 25,000 at ~97% occupancy.
        var result = AiInsightRules.PriceBaseline(currentPricePerDay: 25000, slotCount: 3, occupancyPercent: 96.7, pendingApplications: 0);

        result.Direction.Should().Be(PriceDirections.Raise);
        result.BaselinePricePerDay.Should().Be(28000);
    }

    [Fact]
    public void Low_occupancy_with_little_demand_lowers_the_price()
    {
        // The seed's NVL zone scenario: 30,000 at ~5% occupancy, no queue of applications.
        var result = AiInsightRules.PriceBaseline(currentPricePerDay: 30000, slotCount: 20, occupancyPercent: 5, pendingApplications: 1);

        result.Direction.Should().Be(PriceDirections.Lower);
        result.BaselinePricePerDay.Should().Be(27000);
    }

    [Fact]
    public void Low_occupancy_but_a_real_queue_of_applications_keeps_the_price_instead_of_lowering_it()
    {
        // Under-occupied today, but enough pending applications that the price is not the problem.
        var result = AiInsightRules.PriceBaseline(currentPricePerDay: 30000, slotCount: 10, occupancyPercent: 20, pendingApplications: 5);

        result.Direction.Should().Be(PriceDirections.Keep);
        result.BaselinePricePerDay.Should().Be(30000);
    }

    [Fact]
    public void Middling_occupancy_keeps_the_price()
    {
        var result = AiInsightRules.PriceBaseline(currentPricePerDay: 30000, slotCount: 10, occupancyPercent: 60, pendingApplications: 0);

        result.Direction.Should().Be(PriceDirections.Keep);
        result.BaselinePricePerDay.Should().Be(30000);
    }

    [Theory]
    [InlineData(27499, 27000)]
    [InlineData(27500, 28000)] // .5 rounds away from zero, not to even
    [InlineData(27000, 27000)]
    public void Round1000_rounds_to_the_nearest_thousand_dong(decimal input, decimal expected)
    {
        AiInsightRules.Round1000(input).Should().Be(expected);
    }

    [Fact]
    public void Ceil_and_floor_to_1000_bound_the_allowed_range_outward()
    {
        AiInsightRules.CeilTo1000(24000.01m).Should().Be(25000m);
        AiInsightRules.FloorTo1000(25999.99m).Should().Be(25000m);
    }

    [Fact]
    public void Clamp_price_never_lets_a_model_proposed_price_outside_the_allowed_band()
    {
        AiInsightRules.ClampPrice(proposed: 100000, min: 24000, max: 36000).Should().Be(36000);
        AiInsightRules.ClampPrice(proposed: 1000, min: 24000, max: 36000).Should().Be(24000);
        AiInsightRules.ClampPrice(proposed: 29500, min: 24000, max: 36000).Should().Be(30000);
    }
    #endregion
}
