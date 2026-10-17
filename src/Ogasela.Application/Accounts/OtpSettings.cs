namespace Ogasela.Application.Accounts;

public sealed class OtpSettings
{
    public const string SectionName = "Otp";

    /// <summary>
    /// Optional master code (Default_Global_OTP in .env). When set, OtpService accepts it in place
    /// of the generated code for every OTP check in the app - phone verification, user and admin
    /// login, password resets, phone/email changes. Anyone who knows it can pass any account's OTP
    /// step. It is honoured in every environment, Production included - leave it unset to disable it.
    /// Each use is logged as a warning.
    /// </summary>
    public string? MasterCode { get; set; }
}
