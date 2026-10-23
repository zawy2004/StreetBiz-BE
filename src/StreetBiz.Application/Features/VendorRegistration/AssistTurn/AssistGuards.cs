using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.VendorRegistration.AssistTurn;

/// <summary>
/// What the server lets through from the extractor. A value is accepted only if:
///  - its evidence is really in what the vendor said (no invented fields),
///  - it fits the same limits the registration validator applies,
///  - for amounts, dates and widths, VietnameseSpokenValues reads the same words the same way
///    (when it can read them, its reading wins -- the LLM only fills in the rest).
/// </summary>
public static partial class AssistGuards
{
    public const int MaxLocationText = 200;
    public const decimal MaxCapital = 100_000_000_000m; // 100 tỷ: anything above is a mishearing
    public const int MaxLabor = 100;
    public const int MaxStartDaysAhead = 366;
    public const decimal MinAiConfidence = 0.5m;

    /// <summary>The draft field a question is about (null for the opening, which can fill several).</summary>
    public static string? FieldFor(string questionKey) => questionKey switch
    {
        AssistQuestionKeys.BusinessLine => AssistFields.BusinessLine,
        AssistQuestionKeys.VendorType => AssistFields.VendorType,
        AssistQuestionKeys.Location => AssistFields.LocationText,
        AssistQuestionKeys.Address => AssistFields.DeclaredAddress,
        AssistQuestionKeys.Capital => AssistFields.CapitalAmount,
        AssistQuestionKeys.Labor => AssistFields.LaborCount,
        AssistQuestionKeys.StartDate => AssistFields.PlannedStartDate,
        AssistQuestionKeys.ContactAddress => AssistFields.ContactSameAsPermanent,
        AssistQuestionKeys.CartWidth => AssistFields.CartWidthMeters,
        _ => null,
    };

    public static List<AssistFieldProposal> Accept(
        string questionKey, string? transcript, IReadOnlyList<ExtractedField> fields, DateOnly today, List<string> warnings)
    {
        var said = transcript ?? string.Empty;
        var asked = FieldFor(questionKey);
        var byField = fields
            .Where(f => f.Value is not null)
            .GroupBy(f => f.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var accepted = new List<AssistFieldProposal>();

        // Text fields: must be backed by words the vendor actually said.
        foreach (var (field, max, label) in new[]
                 {
                     (AssistFields.BusinessLine, RegistrationFieldLimits.BusinessLine, "món bán"),
                     (AssistFields.LocationText, MaxLocationText, "chỗ bán"),
                     (AssistFields.DeclaredAddress, RegistrationFieldLimits.DeclaredAddress, "địa chỉ cửa hàng"),
                 })
        {
            if (!byField.TryGetValue(field, out var f))
            {
                continue;
            }

            var value = Clean(f.Value);
            if (value.Length == 0 || value.Length > max || f.Confidence < MinAiConfidence)
            {
                continue;
            }

            if (!SaidIt(said, f.Evidence))
            {
                // Shown to the vendor: their words, not the field's code name.
                warnings.Add($"Trợ lý chưa chắc phần {label} nên chưa điền.");
                continue;
            }

            // "bún bò, bánh mì" -> "Bún bò, bánh mì": it is printed on the registration as written.
            if (field == AssistFields.BusinessLine)
            {
                value = char.ToUpper(value[0], CultureInfo.GetCultureInfo("vi-VN")) + value[1..];
            }

            accepted.Add(new AssistFieldProposal(field, value, f.Confidence, f.Evidence, null, false, AssistProposalSources.Ai));
        }

        if (byField.TryGetValue(AssistFields.VendorType, out var type) && VendorTypes.All.Contains(type.Value!)
            && type.Confidence >= MinAiConfidence && SaidIt(said, type.Evidence))
        {
            accepted.Add(new AssistFieldProposal(AssistFields.VendorType, type.Value!, type.Confidence, type.Evidence, null, false, AssistProposalSources.Ai));
        }

        if (byField.TryGetValue(AssistFields.Hours, out var hours) && TryParseHours(hours.Value, out _, out _) && SaidIt(said, hours.Evidence))
        {
            accepted.Add(new AssistFieldProposal(AssistFields.Hours, hours.Value!, hours.Confidence, hours.Evidence, null, false, AssistProposalSources.Ai));
        }

        // Amount: the parser's reading of the evidence (or of the whole answer when the amount was
        // what was asked) is authoritative; the LLM's number is used only when the parser cannot read it.
        var capital = Numeric(byField, AssistFields.CapitalAmount, asked, said,
            VietnameseSpokenValues.ParseMoney, v => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null);
        if (capital is { } c && c.Value >= 0 && c.Value <= MaxCapital)
        {
            var amount = decimal.Truncate(c.Value);
            accepted.Add(new AssistFieldProposal(AssistFields.CapitalAmount, amount.ToString(CultureInfo.InvariantCulture), c.Confidence, c.Evidence,
                VietnameseSpokenValues.ReadMoney(amount), true, c.Source));
        }

        if (byField.TryGetValue(AssistFields.LaborCount, out var labor)
            && int.TryParse(labor.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var people)
            && people is >= 0 and <= MaxLabor && SaidIt(said, labor.Evidence))
        {
            accepted.Add(new AssistFieldProposal(AssistFields.LaborCount, people.ToString(CultureInfo.InvariantCulture), labor.Confidence, labor.Evidence,
                VietnameseSpokenValues.ReadPeople(people), true, AssistProposalSources.Ai));
        }
        else if (asked == AssistFields.LaborCount && Regex.IsMatch(Fold(said), @"\bmot minh\b"))
        {
            accepted.Add(new AssistFieldProposal(AssistFields.LaborCount, "1", 1, said, VietnameseSpokenValues.ReadPeople(1), true, AssistProposalSources.Parser));
        }
        else if (asked == AssistFields.LaborCount && Fold(said).Contains("nguoi", StringComparison.Ordinal)
                 && VietnameseSpokenValues.ParseSmallNumber(said) is { } counted and >= 1 and <= MaxLabor)
        {
            // "3 người" with no extractor answer: the last number said is the count.
            accepted.Add(new AssistFieldProposal(AssistFields.LaborCount, counted.ToString(CultureInfo.InvariantCulture), 1, said,
                VietnameseSpokenValues.ReadPeople(counted), true, AssistProposalSources.Parser));
        }

        var start = Numeric(byField, AssistFields.PlannedStartDate, asked, said,
            p => VietnameseSpokenValues.ParseStartDate(p, today) is { } d ? d.DayNumber : null,
            v => DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.DayNumber : null);
        if (start is not null)
        {
            var date = DateOnly.FromDayNumber((int)start.Value.Value);
            if (date >= today && date <= today.AddDays(MaxStartDaysAhead))
            {
                accepted.Add(new AssistFieldProposal(AssistFields.PlannedStartDate, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    start.Value.Confidence, start.Value.Evidence, VietnameseSpokenValues.ReadDate(date), true, start.Value.Source));
            }
            else
            {
                warnings.Add("Ngày bắt đầu phải từ hôm nay đến trong vòng một năm.");
            }
        }

        var width = Numeric(byField, AssistFields.CartWidthMeters, asked, said,
            VietnameseSpokenValues.ParseMeters,
            v => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d is >= 0.2m and <= 5m ? d : null);
        if (width is not null)
        {
            accepted.Add(new AssistFieldProposal(AssistFields.CartWidthMeters, width.Value.Value.ToString(CultureInfo.InvariantCulture),
                width.Value.Confidence, width.Value.Evidence, VietnameseSpokenValues.ReadMeters(width.Value.Value), false, width.Value.Source));
        }

        // Yes/no: only when it was the question.
        if (asked == AssistFields.ContactSameAsPermanent)
        {
            var yesNo = byField.TryGetValue(AssistFields.ContactSameAsPermanent, out var contact) && contact.Value is "true" or "false"
                ? contact.Value
                : YesNo(said);
            if (yesNo is not null)
            {
                accepted.Add(new AssistFieldProposal(AssistFields.ContactSameAsPermanent, yesNo, contact?.Confidence ?? 1, contact?.Evidence ?? said,
                    null, false, contact is null ? AssistProposalSources.Parser : AssistProposalSources.Ai));
            }
        }

        return accepted;
    }

    private static (decimal Value, decimal Confidence, string Evidence, string Source)? Numeric(
        IReadOnlyDictionary<string, ExtractedField> byField, string field, string? asked, string said,
        Func<string, decimal?> parse, Func<string, decimal?> readAi)
    {
        byField.TryGetValue(field, out var ai);
        var evidence = ai?.Evidence;
        var fromEvidence = !string.IsNullOrWhiteSpace(evidence) && SaidIt(said, evidence) ? parse(evidence) : null;
        if (fromEvidence is not null)
        {
            return (fromEvidence.Value, 1, evidence!, AssistProposalSources.Parser);
        }

        if (asked == field && parse(said) is { } fromAnswer)
        {
            return (fromAnswer, 1, said, AssistProposalSources.Parser);
        }

        if (ai is not null && readAi(ai.Value!) is { } fromAi && ai.Confidence >= MinAiConfidence && SaidIt(said, ai.Evidence))
        {
            return (fromAi, ai.Confidence, ai.Evidence, AssistProposalSources.Ai);
        }

        return null;
    }

    /// <summary>
    /// Only the opening question may fill anything. Any other answer may set the field it was asked
    /// about, and otherwise only fill fields that are still empty -- an aside never overwrites what
    /// the vendor already gave (Gate 0: an address answer also yielded a "location", which would have
    /// thrown away the spot already confirmed on the map).
    /// </summary>
    public static List<AssistFieldProposal> KeepRelevant(List<AssistFieldProposal> proposals, string questionKey, AssistDraft draft)
    {
        if (questionKey == AssistQuestionKeys.Opening)
        {
            return proposals;
        }

        var asked = FieldFor(questionKey);
        return proposals.Where(p => p.Field == asked || IsEmpty(p.Field, draft)).ToList();
    }

    private static bool IsEmpty(string field, AssistDraft d) => field switch
    {
        AssistFields.BusinessLine => string.IsNullOrWhiteSpace(d.BusinessLine),
        AssistFields.VendorType => string.IsNullOrWhiteSpace(d.VendorType),
        AssistFields.LocationText => string.IsNullOrWhiteSpace(d.LocationText) && !d.LocationConfirmed,
        AssistFields.DeclaredAddress => string.IsNullOrWhiteSpace(d.DeclaredAddress),
        AssistFields.CapitalAmount => d.CapitalAmount is null,
        AssistFields.LaborCount => d.LaborCount is null,
        AssistFields.PlannedStartDate => d.PlannedStartDate is null,
        AssistFields.ContactSameAsPermanent => d.ContactSameAsPermanent is null,
        AssistFields.Hours => string.IsNullOrWhiteSpace(d.Hours),
        AssistFields.CartWidthMeters => d.CartWidthMeters is null,
        _ => false,
    };

    /// <summary>"06:00-10:00" or "18:00-" (no closing time said).</summary>
    public static bool TryParseHours(string? hours, out TimeOnly from, out TimeOnly? to)
    {
        from = default;
        to = null;
        var m = hours is null ? Match.Empty : HoursPattern().Match(hours);
        if (!m.Success || !TimeOnly.TryParseExact(m.Groups[1].Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out from))
        {
            return false;
        }

        if (m.Groups[2].Success)
        {
            if (!TimeOnly.TryParseExact(m.Groups[2].Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                return false;
            }

            to = end;
        }

        return true;
    }

    /// <summary>True when the evidence is (loosely) a piece of what was said.</summary>
    public static bool SaidIt(string said, string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        var e = Fold(evidence);
        return e.Length > 0 && Fold(said).Contains(e, StringComparison.Ordinal);
    }

    /// <summary>Lower case, no accents, no punctuation, single spaces: "Chợ Cồn," == "cho con".</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(ch switch { 'đ' => 'd', 'Đ' => 'd', _ => char.ToLowerInvariant(ch) });
        }

        var plain = Regex.Replace(sb.ToString(), @"[^\p{L}\p{Nd}]+", " ");
        return Regex.Replace(plain, @"\s+", " ").Trim();
    }

    private static string? YesNo(string said)
    {
        var s = Fold(said);
        if (Regex.IsMatch(s, @"\b(khong|cho khac|o tro|thue nha)\b"))
        {
            return "false";
        }

        return Regex.IsMatch(s, @"\b(dung|phai|u|vang|da dung|chinh xac)\b") ? "true" : null;
    }

    private static string Clean(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    [GeneratedRegex(@"^(\d{2}:\d{2})-(\d{2}:\d{2})?$")]
    private static partial Regex HoursPattern();
}
