namespace StreetBiz.Application.Features.VendorRegistration.AssistTurn;

/// <summary>
/// X01 "Kể chuyện là xong" -- the registration interview a vendor answers by voice. The server is
/// stateless: the client keeps the draft and sends its non-sensitive part back on every turn.
/// Nothing here is ever written to a registration; the vendor's own tap on "Đúng, nộp" files it
/// through the existing REG-01/REG-02 commands (BR-41).
/// </summary>
public static class AssistQuestionKeys
{
    public const string Opening = "OPENING";
    public const string BusinessLine = "BUSINESS_LINE";
    public const string VendorType = "VENDOR_TYPE";
    public const string Location = "LOCATION";
    public const string Ward = "WARD";
    public const string Address = "ADDRESS";
    public const string Capital = "CAPITAL";
    public const string Labor = "LABOR";
    public const string StartDate = "START_DATE";
    public const string ContactAddress = "CONTACT_ADDRESS";
    public const string CartWidth = "CART_WIDTH";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Opening, BusinessLine, VendorType, Location, Ward, Address, Capital, Labor, StartDate, ContactAddress, CartWidth,
    };
}

/// <summary>Draft field names as the client stores them (camelCase, the wizard's own names).</summary>
public static class AssistFields
{
    public const string BusinessLine = "businessLine";
    public const string VendorType = "vendorType";
    public const string LocationText = "locationText";
    public const string DeclaredAddress = "declaredAddress";
    public const string CapitalAmount = "capitalAmount";
    public const string LaborCount = "laborCount";
    public const string PlannedStartDate = "plannedStartDate";
    public const string ContactSameAsPermanent = "contactSameAsPermanent";
    public const string Hours = "hours";
    public const string CartWidthMeters = "cartWidthMeters";

    /// <summary>Values the vendor must confirm with a tap after hearing them read back.</summary>
    public static readonly IReadOnlySet<string> NeedTapConfirmation = new HashSet<string>(StringComparer.Ordinal)
    {
        CapitalAmount, LaborCount, PlannedStartDate,
    };
}

/// <summary>How the assistant addresses the vendor; chosen on the client from the CCCD (age, gender).</summary>
public static class AssistSalutations
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { "bạn", "cô", "chú", "anh", "chị" };
}

/// <summary>
/// The part of the wizard draft the server may see. It never holds anything read from the CCCD
/// (name, number, birth date, addresses) -- those stay on the device until the vendor files.
/// </summary>
public sealed record AssistDraft(
    string? BusinessLine = null,
    string? VendorType = null,
    string? LocationText = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    bool LocationConfirmed = false,
    bool LocationSkipped = false,
    int? WardUnitId = null,
    string? DeclaredAddress = null,
    decimal? CapitalAmount = null,
    int? LaborCount = null,
    DateOnly? PlannedStartDate = null,
    bool? ContactSameAsPermanent = null,
    string? Hours = null,
    decimal? CartWidthMeters = null,
    bool CartWidthSkipped = false,
    IReadOnlyList<string>? Confirmed = null,
    IReadOnlyDictionary<string, int>? Attempts = null,
    string Salutation = "bạn");

/// <summary>One value the assistant heard. <see cref="Value"/> is in the draft's own format:
/// digits for amounts, yyyy-MM-dd for dates, "true"/"false", or the enum code.</summary>
public sealed record AssistFieldProposal(
    string Field,
    string Value,
    decimal Confidence,
    string Evidence,
    string? ReadBack,
    bool NeedsConfirmation,
    string Source);

public static class AssistProposalSources
{
    /// <summary>Read by VietnameseSpokenValues -- deterministic.</summary>
    public const string Parser = "PARSER";

    /// <summary>Extracted by the LLM and passed the server's guards.</summary>
    public const string Ai = "AI";
}

public sealed record AssistChoice(string Value, string Label);

public static class AssistInputs
{
    public const string Voice = "VOICE";
    public const string Choice = "CHOICE";
    public const string Confirm = "CONFIRM";
    public const string Manual = "MANUAL";

    /// <summary>Confirm or pick the selling spot on a map (drag the pin, or tap).</summary>
    public const string Map = "MAP";
}

/// <param name="Key">An <see cref="AssistQuestionKeys"/> value, or "CONFIRM:{field}" for a read-back.</param>
/// <param name="Input">An <see cref="AssistInputs"/> value: how the client should take the answer.</param>
/// <param name="SuggestedCount">How many of the first <paramref name="Choices"/> are the assistant's
/// guesses (the ward question lists every ward, guesses first; the client shows those as buttons
/// and lets the vendor search the rest).</param>
public sealed record AssistQuestion(
    string Key,
    string Text,
    string Input,
    IReadOnlyList<AssistChoice> Choices,
    string? Field = null,
    bool Skippable = false,
    int SuggestedCount = 0);

public sealed record AssistWardOption(int UnitId, string UnitName, string Reason);

public static class AssistWardReasons
{
    public const string SlotsNearby = "SLOTS_NEARBY";
    public const string NameMatch = "NAME_MATCH";
}

public sealed record AssistLocation(
    string Query,
    decimal Latitude,
    decimal Longitude,
    string Label,
    IReadOnlyList<AssistWardOption> WardOptions);

public sealed record AssistSlotPreview(
    long SlotId,
    string SlotCode,
    string ZoneName,
    int WardUnitId,
    double DistanceMeters,
    decimal? WidthMeters,
    decimal? LengthMeters,
    decimal MonthlyPrice,
    TimeOnly? AvailableFrom,
    TimeOnly? AvailableTo,
    bool? FitsCart,
    bool? FitsHours);

public sealed record AssistTurnResult(
    string? Transcript,
    IReadOnlyList<AssistFieldProposal> Proposals,
    AssistLocation? Location,
    IReadOnlyList<AssistSlotPreview> SlotPreview,
    AssistQuestion? NextQuestion,
    IReadOnlyList<string> Warnings,
    bool Done,
    bool IsAiGenerated,
    /// <summary>The client's draft with this turn applied (attempt counters included); the client
    /// keeps it as the interview state. Still nothing is filed until the vendor taps "Đúng, nộp".</summary>
    AssistDraft Draft);

/// <summary>What the vendor's tap on "Đúng, nộp" records about the AI draft (BR-41/46).</summary>
public sealed record AssistConfirmationRequest(
    int Turns,
    IReadOnlyList<string>? AiFields,
    IReadOnlyList<string>? EditedFields,
    int DurationSeconds);

public sealed record AssistConfirmationDto(long AiLogId, long RegistrationId, DateTime ConfirmedAt);
