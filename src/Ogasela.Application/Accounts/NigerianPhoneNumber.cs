using System.Text.RegularExpressions;

namespace Ogasela.Application.Accounts;

/// <summary>
/// Matches Nigerian mobile numbers in local (0XXXXXXXXXX) or international
/// (+234XXXXXXXXXX / 234XXXXXXXXXX) format.
/// </summary>
public static partial class NigerianPhoneNumber
{
    public static bool IsValid(string? phone) =>
        !string.IsNullOrWhiteSpace(phone) && Regex().IsMatch(phone);

    /// <summary>
    /// Converts a valid Nigerian number to international format without a leading "+"
    /// (e.g. "08031234567" -&gt; "2348031234567"), the format most Nigerian SMS gateways expect.
    /// Assumes <see cref="IsValid"/> has already been checked.
    /// </summary>
    public static string ToInternationalFormat(string phone)
    {
        if (phone.StartsWith("+234", StringComparison.Ordinal))
        {
            return phone[1..];
        }

        if (phone.StartsWith("234", StringComparison.Ordinal))
        {
            return phone;
        }

        // Local format: leading "0" -> "234"
        return "234" + phone[1..];
    }

    /// <summary>
    /// Converts a valid Nigerian number to the local format the app stores (e.g. "+2348031234567"
    /// -&gt; "08031234567"). Assumes <see cref="IsValid"/> has already been checked.
    /// </summary>
    public static string ToLocalFormat(string phone) => "0" + ToInternationalFormat(phone)[3..];

    [GeneratedRegex(@"^(\+234|234|0)[789][01]\d{8}$")]
    private static partial Regex Regex();
}
