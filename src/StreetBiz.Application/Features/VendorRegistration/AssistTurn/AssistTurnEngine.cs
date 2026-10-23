using System.Globalization;
using System.Text.RegularExpressions;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.DTOs.AdministrativeUnits;
using StreetBiz.Application.DTOs.Sidewalk;
using StreetBiz.Application.Features.AdministrativeUnits.ListWards;
using StreetBiz.Application.Features.SidewalkSlots.SearchSlots;

namespace StreetBiz.Application.Features.VendorRegistration.AssistTurn;

/// <summary>What one answer yielded: the vendor's words and the extractor's (unchecked) fields.</summary>
/// <param name="IsAiGenerated">True when the fields came from a model (and so must carry the [AI] label).</param>
public sealed record AssistHeard(string? Transcript, IReadOnlyList<ExtractedField> Fields, bool IsAiGenerated);

/// <summary>
/// One step of the interview once an answer was heard (or a tap changed the draft): keeps what the
/// guards accept, looks the spoken place up, previews nearby slots and wards, and asks the planner
/// what comes next. Shared by the tap-to-talk turn (v1) and the live conversation (v2), so both
/// fill the draft by exactly the same rules.
/// </summary>
public sealed class AssistTurnEngine(IGeocodingService geocoding, ISender sender)
{
    /// <summary>Slots within this distance of the spoken spot are offered as a preview.</summary>
    public const double SlotPreviewRadiusMeters = 500;

    public Task<IReadOnlyList<WardDto>> WardsAsync(CancellationToken ct) => sender.Send(new ListWardsQuery(), ct);

    /// <param name="questionKey">The question the answer replies to (an <see cref="AssistQuestionKeys"/> value).</param>
    /// <param name="heard">Null when nothing was answered: only plan (first question, or after a tap).</param>
    /// <param name="warnings">Shown to the vendor; the caller may already have added some.</param>
    public async Task<AssistTurnResult> AdvanceAsync(
        string questionKey,
        AssistDraft draft,
        AssistHeard? heard,
        DateOnly today,
        IReadOnlyList<WardDto> wards,
        List<string> warnings,
        CancellationToken ct)
    {
        var proposals = new List<AssistFieldProposal>();
        if (heard is not null)
        {
            proposals = AssistGuards.KeepRelevant(
                AssistGuards.Accept(questionKey, heard.Transcript, heard.Fields, today, warnings),
                questionKey,
                draft);
            draft = Merge(draft, proposals, questionKey);
        }

        // Where the vendor sells: look the spoken place up, then which slots and wards are near it.
        AssistLocation? location = null;
        IReadOnlyList<AssistSlotPreview> slotPreview = [];
        var locationChanged = proposals.Any(p => p.Field == AssistFields.LocationText);
        if (locationChanged && !string.IsNullOrWhiteSpace(draft.LocationText))
        {
            var hits = await geocoding.SearchAsync($"{draft.LocationText}, Đà Nẵng", 3, ct);
            var hit = hits.FirstOrDefault();
            if (hit is null)
            {
                warnings.Add($"Chưa tìm thấy \"{draft.LocationText}\" trên bản đồ.");
                draft = draft with { Latitude = null, Longitude = null, LocationConfirmed = false };
            }
            else
            {
                draft = draft with { Latitude = hit.Latitude, Longitude = hit.Longitude, LocationConfirmed = false, WardUnitId = null };
                location = new AssistLocation(draft.LocationText!, hit.Latitude, hit.Longitude, hit.DisplayName ?? draft.LocationText!, []);
            }
        }

        IReadOnlyList<AssistWardOption> wardOptions = [];
        if (draft.Latitude is { } lat && draft.Longitude is { } lng)
        {
            var nearby = await sender.Send(
                new SearchSlotsQuery(lat, lng, SlotPreviewRadiusMeters, null, null, null, null, null, null, Take: 30),
                ct);
            slotPreview = nearby.Take(3).Select(s => Preview(s, draft)).ToList();

            // The ward question usually comes on a later turn (after the pin is confirmed), which
            // carries no fresh lookup: name the wards from the spoken place's label again. The
            // geocoder caches it, so this costs no second request.
            var mapLabel = location?.Label;
            if (mapLabel is null && draft.WardUnitId is null && !string.IsNullOrWhiteSpace(draft.LocationText))
            {
                mapLabel = (await geocoding.SearchAsync($"{draft.LocationText}, Đà Nẵng", 3, ct))
                    .FirstOrDefault()?.DisplayName;
            }

            wardOptions = WardOptions(nearby, mapLabel, wards);
            if (location is not null)
            {
                location = location with { WardOptions = wardOptions };
            }
        }

        if (heard is not null && !proposals.Any(p => AssistGuards.FieldFor(questionKey) is { } f && p.Field == f)
            && questionKey != AssistQuestionKeys.Opening)
        {
            // Nothing usable for what was asked: count the try so the planner can switch to typing.
            draft = draft with { Attempts = Bump(draft.Attempts, questionKey) };
            if (!string.IsNullOrWhiteSpace(heard.Transcript))
            {
                warnings.Add("Trợ lý chưa nghe ra câu trả lời.");
            }
        }
        else if (heard is not null && questionKey == AssistQuestionKeys.Opening)
        {
            draft = draft with { Attempts = Bump(draft.Attempts, AssistQuestionKeys.Opening) };
        }

        var next = InterviewPlanner.Next(draft, wardOptions, wards);

        return new AssistTurnResult(
            heard?.Transcript,
            proposals,
            location,
            slotPreview,
            next,
            warnings,
            next is null,
            IsAiGenerated: heard?.IsAiGenerated ?? false,
            Draft: draft);
    }

    /// <summary>Applies accepted values. A changed amount/date loses its earlier confirmation.</summary>
    public static AssistDraft Merge(AssistDraft draft, IReadOnlyList<AssistFieldProposal> proposals, string questionKey)
    {
        var confirmed = new HashSet<string>(draft.Confirmed ?? [], StringComparer.Ordinal);
        foreach (var p in proposals)
        {
            switch (p.Field)
            {
                case AssistFields.BusinessLine: draft = draft with { BusinessLine = p.Value }; break;
                case AssistFields.VendorType: draft = draft with { VendorType = p.Value }; break;
                case AssistFields.LocationText: draft = draft with { LocationText = p.Value }; break;
                case AssistFields.DeclaredAddress: draft = draft with { DeclaredAddress = p.Value }; break;
                case AssistFields.CapitalAmount:
                    var capital = decimal.Parse(p.Value, CultureInfo.InvariantCulture);
                    if (draft.CapitalAmount != capital) confirmed.Remove(p.Field);
                    draft = draft with { CapitalAmount = capital };
                    break;
                case AssistFields.LaborCount:
                    var labor = int.Parse(p.Value, CultureInfo.InvariantCulture);
                    if (draft.LaborCount != labor) confirmed.Remove(p.Field);
                    draft = draft with { LaborCount = labor };
                    break;
                case AssistFields.PlannedStartDate:
                    var start = DateOnly.ParseExact(p.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (draft.PlannedStartDate != start) confirmed.Remove(p.Field);
                    draft = draft with { PlannedStartDate = start };
                    break;
                case AssistFields.ContactSameAsPermanent: draft = draft with { ContactSameAsPermanent = p.Value == "true" }; break;
                case AssistFields.Hours: draft = draft with { Hours = p.Value }; break;
                case AssistFields.CartWidthMeters:
                    draft = draft with { CartWidthMeters = decimal.Parse(p.Value, CultureInfo.InvariantCulture) };
                    break;
            }
        }

        return draft with { Confirmed = confirmed.ToList() };
    }

    /// <summary>
    /// Wards to offer, best guess first: the wards that own the nearby slots (a slot always
    /// belongs to exactly one ward), then wards whose name appears in the map label -- the earlier
    /// in the label, the more specific the place, so the better the guess. Labels may still use
    /// the pre-2025 names ("Hòa Cường Bắc, Hải Châu"): matching whole words keeps "Hòa Cường" ahead
    /// of the old district name. The vendor still picks one -- this only orders the buttons.
    /// </summary>
    public static IReadOnlyList<AssistWardOption> WardOptions(
        IReadOnlyList<SidewalkSlotDto> nearbySlots, string? mapLabel, IReadOnlyList<WardDto> wards)
    {
        var byId = wards.ToDictionary(w => w.UnitId);
        var options = nearbySlots
            .GroupBy(s => s.WardUnitId)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(s => s.DistanceMeters ?? double.MaxValue))
            .Where(g => byId.ContainsKey(g.Key))
            .Select(g => new AssistWardOption(g.Key, byId[g.Key].UnitName, AssistWardReasons.SlotsNearby))
            .ToList();

        if (!string.IsNullOrWhiteSpace(mapLabel))
        {
            var label = $" {AssistGuards.Fold(mapLabel)} ";
            var named = new List<(int At, AssistWardOption Option)>();
            foreach (var w in wards)
            {
                if (options.Any(o => o.UnitId == w.UnitId))
                {
                    continue;
                }

                // Only the ward's own name: its parent is the city now (no districts since
                // 1/7/2025), and "Đà Nẵng" is in every label.
                var name = AssistGuards.Fold(Regex.Replace(w.UnitName, @"^(phường|xã|đặc khu)\s+", string.Empty, RegexOptions.IgnoreCase));
                var at = name.Length == 0 ? -1 : label.IndexOf($" {name} ", StringComparison.Ordinal);
                if (at >= 0)
                {
                    named.Add((at, new AssistWardOption(w.UnitId, w.UnitName, AssistWardReasons.NameMatch)));
                }
            }

            options.AddRange(named.OrderBy(n => n.At).Select(n => n.Option));
        }

        return options.Take(4).ToList();
    }

    private static AssistSlotPreview Preview(SidewalkSlotDto s, AssistDraft draft)
    {
        bool? fitsCart = draft.CartWidthMeters is { } cart && s.WidthMeters is { } width ? width >= cart : null;
        bool? fitsHours = null;
        if (AssistGuards.TryParseHours(draft.Hours, out var from, out var to) && s.AvailableFrom is { } open && s.AvailableTo is { } close)
        {
            fitsHours = from >= open && (to is null || to <= close);
        }

        return new AssistSlotPreview(
            s.SlotId, s.SlotCode, s.ZoneName, s.WardUnitId, Math.Round(s.DistanceMeters ?? 0),
            s.WidthMeters, s.LengthMeters, s.MonthlyPrice, s.AvailableFrom, s.AvailableTo, fitsCart, fitsHours);
    }

    private static IReadOnlyDictionary<string, int> Bump(IReadOnlyDictionary<string, int>? attempts, string key)
    {
        var copy = attempts is null ? new Dictionary<string, int>(StringComparer.Ordinal) : new Dictionary<string, int>(attempts, StringComparer.Ordinal);
        copy[key] = copy.TryGetValue(key, out var n) ? n + 1 : 1;
        return copy;
    }
}
