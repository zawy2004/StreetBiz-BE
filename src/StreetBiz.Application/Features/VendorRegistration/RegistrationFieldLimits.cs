namespace StreetBiz.Application.Features.VendorRegistration;

/// <summary>
/// Column sizes of BusinessRegistrations (db/StreetBiz_SQL_Server.sql), shared by the REG-01/04
/// validator and the X01 voice assistant's guards so the two can never disagree.
/// </summary>
public static class RegistrationFieldLimits
{
    public const int DisplayName = 180;
    public const int DeclaredAddress = 500;
    public const int Ethnicity = 50;
    public const int Nationality = 50;
    public const int IdIssuedPlace = 150;
    public const int PermanentAddress = 300;
    public const int ContactAddress = 300;
    public const int BusinessLine = 300;
    public const int BusinessLineCode = 20;
    public const int HouseholdMemberName = 150;
    public const int HouseholdMemberIdNumber = 12;
    public const int HouseholdMemberRelationship = 50;
}
