using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Features.WardConfiguration;

namespace StreetBiz.Application.Features.AiAssistance;

public static class ProposalRecommendations
{
    public const string LikelyFeasible = "LIKELY_FEASIBLE";
    public const string NeedsSurvey = "NEEDS_SURVEY";
    public const string LikelyInfeasible = "LIKELY_INFEASIBLE";
}

public static class ObstructionLevels
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Unknown = "UNKNOWN";

    public static readonly string[] Known = [Low, Medium, High];
}

public static class DriftLevels
{
    public const string Watch = "WATCH";
    public const string Drift = "DRIFT";
}

public static class DriftPatterns
{
    public const string ConsistentDirection = "CONSISTENT_DIRECTION";
    public const string Scattered = "SCATTERED";
}

public static class PriceDirections
{
    public const string Raise = "RAISE";
    public const string Lower = "LOWER";
    public const string Keep = "KEEP";
    public const string InsufficientData = "INSUFFICIENT_DATA";
}

/// <summary>
/// Pure, database-free rules behind AIC-04/06/07: everything here is a plain function of its
/// arguments, so AiInsightRulesTests covers them without a database or an HTTP stub. The AI
/// provider (AiComplianceService) and the orchestration (WardAiInsights) call into this; neither
/// duplicates the decision logic itself.
/// </summary>
public static class AiInsightRules
{
    private const double MinPedestrianWidthMeters = 1.5d;
    private const double MetersPerDegreeLatitude = 111_320d;

    #region AIC-04: proposed-slot feasibility
    /// <summary>Merges a fresh, successfully-parsed model answer with the system's own rule
    /// checks. A BLOCK rule issue always wins over whatever the model said.</summary>
    public static AiProposalAssessment MergeProposal(
        AiProposalSiteInput input,
        decimal? modelWidthMeters,
        string? modelObstructionLevel,
        int modelConfidence,
        IReadOnlyList<string> modelReasons,
        bool usedSatelliteImage,
        bool usedProposalPhoto)
    {
        var confidence = Math.Clamp(modelConfidence, 0, 100);
        var hasBlock = input.RuleChecks.Any(i => i.Severity == PlacementSeverities.Block);

        decimal? remaining = modelWidthMeters.HasValue
            ? Math.Max(0m, modelWidthMeters.Value - (input.WidthMeters ?? 0m))
            : null;

        string recommendation;
        if (hasBlock)
        {
            recommendation = ProposalRecommendations.LikelyInfeasible;
        }
        else if (remaining.HasValue && (double)remaining.Value < MinPedestrianWidthMeters)
        {
            recommendation = confidence >= 50
                ? ProposalRecommendations.LikelyInfeasible
                : ProposalRecommendations.NeedsSurvey;
        }
        else if (remaining.HasValue && (double)remaining.Value >= MinPedestrianWidthMeters && confidence >= 60)
        {
            recommendation = ProposalRecommendations.LikelyFeasible;
        }
        else
        {
            recommendation = ProposalRecommendations.NeedsSurvey;
        }

        var obstructionLevel = modelObstructionLevel?.Trim().ToUpperInvariant();
        if (obstructionLevel is null || !ObstructionLevels.Known.Contains(obstructionLevel))
        {
            obstructionLevel = ObstructionLevels.Unknown;
        }

        var reasons = new List<string>(input.RuleChecks.Select(i => $"[Hệ thống] {i.Message}"));
        reasons.AddRange(modelReasons.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => $"[AI] {r}"));

        return new AiProposalAssessment(
            EstimatedSidewalkWidthMeters: modelWidthMeters,
            RemainingPedestrianWidthMeters: remaining,
            ObstructionLevel: obstructionLevel,
            Recommendation: recommendation,
            Confidence: confidence,
            Reasons: reasons,
            RuleChecks: input.RuleChecks,
            UsedSatelliteImage: usedSatelliteImage,
            UsedProposalPhoto: usedProposalPhoto,
            IsAiGenerated: true);
    }

    /// <summary>No image, no key, or an unparseable model answer: rule checks only, labelled
    /// honestly as not AI-verified (BR-41's "AI never decides" also means "never pretends to").</summary>
    public static AiProposalAssessment FallbackProposalAssessment(AiProposalSiteInput input)
    {
        var hasBlock = input.RuleChecks.Any(i => i.Severity == PlacementSeverities.Block);
        var reasons = new List<string>(input.RuleChecks.Select(i => $"[Hệ thống] {i.Message}"))
        {
            "[Hệ thống — chưa xác minh bằng AI] Chưa ước lượng được bề rộng vỉa hè, cần khảo sát thực địa."
        };

        return new AiProposalAssessment(
            EstimatedSidewalkWidthMeters: null,
            RemainingPedestrianWidthMeters: null,
            ObstructionLevel: ObstructionLevels.Unknown,
            Recommendation: hasBlock ? ProposalRecommendations.LikelyInfeasible : ProposalRecommendations.NeedsSurvey,
            Confidence: 0,
            Reasons: reasons,
            RuleChecks: input.RuleChecks,
            UsedSatelliteImage: false,
            UsedProposalPhoto: false,
            IsAiGenerated: false);
    }
    #endregion

    #region AIC-06: geofence drift
    public sealed record DriftScan(long ScanId, DateTime ScannedAt, string ScanContext, double Latitude, double Longitude);

    public sealed record DriftEvaluation(
        int ScanCount,
        int OffSiteCount,
        double MaxDistanceMeters,
        double? MeanOffsetMeters,
        double? MeanOffsetBearingDegrees,
        string Level,
        string Pattern,
        IReadOnlyList<(DriftScan Scan, double DistanceMeters)> ScansWithDistance);

    /// <summary>Null means nothing to flag -- every scan was within tolerance.</summary>
    public static DriftEvaluation? EvaluateDrift(
        double anchorLat, double anchorLon, IReadOnlyList<DriftScan> scans)
    {
        if (scans.Count == 0)
        {
            return null;
        }

        var withDistance = scans
            .Select(s => (Scan: s, DistanceMeters: GeoMath.DistanceMeters(anchorLat, anchorLon, s.Latitude, s.Longitude)))
            .ToList();

        var offSite = withDistance.Where(x => x.DistanceMeters > GeofenceDriftDefaults.ToleranceMeters).ToList();
        if (offSite.Count == 0)
        {
            return null;
        }

        var level = offSite.Count >= 2 ? DriftLevels.Drift : DriftLevels.Watch;
        var maxDistance = withDistance.Max(x => x.DistanceMeters);

        var pattern = DriftPatterns.Scattered;
        double? meanOffset = null;
        double? bearing = null;

        if (level == DriftLevels.Drift)
        {
            var metersPerDegreeLongitude = MetersPerDegreeLatitude * Math.Cos(anchorLat * Math.PI / 180d);
            var offsets = offSite
                .Select(x => (
                    Dx: (x.Scan.Longitude - anchorLon) * metersPerDegreeLongitude,
                    Dy: (x.Scan.Latitude - anchorLat) * MetersPerDegreeLatitude))
                .ToList();

            var centroidDx = offsets.Average(o => o.Dx);
            var centroidDy = offsets.Average(o => o.Dy);
            var centroidMagnitude = Math.Sqrt(centroidDx * centroidDx + centroidDy * centroidDy);
            var meanDistanceFromCentroid = offsets.Average(o =>
                Math.Sqrt(Math.Pow(o.Dx - centroidDx, 2) + Math.Pow(o.Dy - centroidDy, 2)));

            // A tight cluster far from the slot reads as "moved to a new spot and stayed there";
            // a cluster whose own spread rivals its distance from the slot reads as noise instead.
            if (centroidMagnitude > GeofenceDriftDefaults.ToleranceMeters
                && meanDistanceFromCentroid < centroidMagnitude * 0.5)
            {
                pattern = DriftPatterns.ConsistentDirection;
                meanOffset = centroidMagnitude;
                var bearingDegrees = Math.Atan2(centroidDx, centroidDy) * 180d / Math.PI;
                bearing = (bearingDegrees + 360d) % 360d;
            }
        }

        return new DriftEvaluation(withDistance.Count, offSite.Count, maxDistance, meanOffset, bearing, level, pattern, withDistance);
    }

    private static readonly string[] CompassWords =
        ["Bắc", "Đông Bắc", "Đông", "Đông Nam", "Nam", "Tây Nam", "Tây", "Tây Bắc"];

    public static string CompassWord(double bearingDegrees)
    {
        var index = (int)Math.Round(bearingDegrees / 45d, MidpointRounding.AwayFromZero) % CompassWords.Length;
        return CompassWords[index];
    }

    /// <summary>Deterministic text shown for every flagged permit -- the Groq narrative, when it
    /// comes back, is shown alongside this rather than instead of it.</summary>
    public static string DriftExplanation(
        int offSiteCount, int scanCount, string pattern, double? meanOffsetMeters, double? bearingDegrees)
    {
        var detail = pattern == DriftPatterns.ConsistentDirection && bearingDegrees.HasValue
            ? $", cùng hướng {CompassWord(bearingDegrees.Value)} khoảng {Math.Round(meanOffsetMeters ?? 0):0} m — có thể hộ đã dời vị trí"
            : ", không theo một hướng cố định — có thể do sai số GPS hoặc hộ di chuyển quanh khu vực";

        return $"[Hệ thống — chưa xác minh bằng AI] {offSiteCount}/{scanCount} lần quét trong " +
               $"{GeofenceDriftDefaults.WindowDays} ngày cách ô cấp phép quá {GeofenceDriftDefaults.ToleranceMeters:0} m{detail}; " +
               "đề nghị kiểm tra thực địa.";
    }
    #endregion

    #region AIC-07: zone price suggestion
    public sealed record PriceBaselineResult(string Direction, decimal BaselinePricePerDay, decimal MinAllowedPricePerDay, decimal MaxAllowedPricePerDay);

    /// <summary>+-10% baseline from occupancy, clamped to +-20% of the current price and rounded
    /// to the nearest 1,000 VND. Demand (pendingApplications) only gates the LOWER direction --
    /// an under-occupied zone with a queue of pending applications is not actually overpriced.</summary>
    public static PriceBaselineResult PriceBaseline(
        decimal currentPricePerDay, int slotCount, double occupancyPercent, int pendingApplications)
    {
        if (slotCount <= 0)
        {
            return new PriceBaselineResult(PriceDirections.InsufficientData, currentPricePerDay, currentPricePerDay, currentPricePerDay);
        }

        var min = CeilTo1000(currentPricePerDay * 0.8m);
        var max = FloorTo1000(currentPricePerDay * 1.2m);

        string direction;
        decimal baseline;
        if (occupancyPercent >= 85d)
        {
            direction = PriceDirections.Raise;
            baseline = currentPricePerDay * 1.10m;
        }
        else if (occupancyPercent < 40d && pendingApplications < slotCount * 0.2d)
        {
            direction = PriceDirections.Lower;
            baseline = currentPricePerDay * 0.90m;
        }
        else
        {
            direction = PriceDirections.Keep;
            baseline = currentPricePerDay;
        }

        baseline = Math.Clamp(Round1000(baseline), min, max);
        return new PriceBaselineResult(direction, baseline, min, max);
    }

    public static decimal Round1000(decimal value) => Math.Round(value / 1000m, MidpointRounding.AwayFromZero) * 1000m;
    public static decimal CeilTo1000(decimal value) => Math.Ceiling(value / 1000m) * 1000m;
    public static decimal FloorTo1000(decimal value) => Math.Floor(value / 1000m) * 1000m;

    /// <summary>Clamps a model-proposed price into the already-computed allowed range and rounds
    /// it -- the model is never trusted to have respected the range or the rounding itself.</summary>
    public static decimal ClampPrice(decimal proposed, decimal min, decimal max) =>
        Round1000(Math.Clamp(proposed, min, max));
    #endregion
}

public static class GeofenceDriftDefaults
{
    public const double ToleranceMeters = 25.0;
    public const int WindowDays = 30;
}
