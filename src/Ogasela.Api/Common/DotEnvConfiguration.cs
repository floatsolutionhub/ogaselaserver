using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace Ogasela.Api.Common;

/// <summary>
/// Loads credentials from the repo-root .env for local `dotnet run`, so .env is the single place
/// secrets live (no .NET User Secrets). docker-compose.yml maps the same .env names into the
/// container itself - .env is excluded from the image (.dockerignore), so this no-ops there.
/// Keep <see cref="Map"/> in step with docker-compose.yml's environment block.
/// </summary>
public static class DotEnvConfiguration
{
    /// <summary>.env name -> configuration key.</summary>
    private static readonly Dictionary<string, string> Map = new()
    {
        ["JWT_SECRET"] = "Jwt:Secret",
        ["Default_Global_OTP"] = "Otp:MasterCode",
        ["TERMII_API_KEY"] = "Termii:ApiKey",
        ["EMAIL_SMTP_KEY"] = "Email:SmtpKey",
        ["EMAIL_SMTP_PORT"] = "Email:Port",
        ["AWS_REGION"] = "Aws:Region",
        ["AWS_ACCESS_KEY_ID"] = "Aws:AccessKeyId",
        ["AWS_SECRET_ACCESS_KEY"] = "Aws:SecretAccessKey",
        ["AWS_S3_BUCKET_NAME"] = "Aws:S3BucketName",
        ["AWS_REKOGNITION_FACE_MATCH_THRESHOLD"] = "Aws:Rekognition:FaceMatchThreshold",
        ["PAYSTACK_SECRET_KEY"] = "Payments:Paystack:SecretKey",
        ["FLUTTERWAVE_SECRET_KEY"] = "Payments:Flutterwave:SecretKey",
        ["FLUTTERWAVE_SECRET_HASH"] = "Payments:Flutterwave:SecretHash",
        ["TURNSTILE_SECRET_KEY"] = "Turnstile:SecretKey",
        ["FIREBASE_PROJECT_ID"] = "Firebase:ProjectId",
        ["FIREBASE_SERVICE_ACCOUNT_JSON"] = "Firebase:ServiceAccountJson",
        ["GEMINI_API_KEY"] = "Ai:Gemini:ApiKey",
        ["ANTHROPIC_API_KEY"] = "Ai:Anthropic:ApiKey",
        ["FACEBOOK_ADS_APP_ID"] = "AdIntegrations:Facebook:AppId",
        ["FACEBOOK_ADS_APP_SECRET"] = "AdIntegrations:Facebook:AppSecret",
        ["TIKTOK_ADS_APP_ID"] = "AdIntegrations:TikTok:AppId",
        ["TIKTOK_ADS_APP_SECRET"] = "AdIntegrations:TikTok:AppSecret",
        ["GOOGLE_IOS_CLIENT_ID"] = "SocialAuth:Google:ClientIds:0",
        ["GOOGLE_ANDROID_CLIENT_ID"] = "SocialAuth:Google:ClientIds:1",
        ["GOOGLE_WEB_CLIENT_ID"] = "SocialAuth:Google:ClientIds:2",
        ["FACEBOOK_LOGIN_APP_ID"] = "SocialAuth:Facebook:AppId",
        ["FACEBOOK_LOGIN_APP_SECRET"] = "SocialAuth:Facebook:AppSecret",
        ["SUPERADMIN_SEED_NAME"] = "SuperAdminSeed:Name",
        ["SUPERADMIN_SEED_PHONE"] = "SuperAdminSeed:Phone",
        ["SUPERADMIN_SEED_EMAIL"] = "SuperAdminSeed:Email",
        ["SUPERADMIN_SEED_PASSWORD"] = "SuperAdminSeed:Password",
    };

    /// <summary>
    /// Adds the mapped .env values on top of the appsettings files but beneath real environment
    /// variables and command-line/host settings, so those still override .env (e.g. in CI or tests).
    /// Empty .env values are skipped so appsettings defaults still apply.
    /// </summary>
    public static void AddDotEnv(this ConfigurationManager configuration, string contentRoot)
    {
        var path = FindDotEnv(contentRoot);
        if (path is null)
        {
            return;
        }

        var values = new Dictionary<string, string?>();
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());
            if (value.Length == 0 || !Map.TryGetValue(name, out var key))
            {
                continue;
            }

            values[key] = value;
        }

        var source = new MemoryConfigurationSource { InitialData = values };
        var sources = configuration.Sources;
        var lastJsonIndex = -1;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                lastJsonIndex = i;
            }
        }

        // Straight after the last appsettings*.json source: above appsettings, below the
        // environment-variables and command-line sources that follow it.
        sources.Insert(lastJsonIndex + 1, source);
    }

    /// <summary>Walks up from the content root (src/Ogasela.Api) to the repo root's .env.</summary>
    private static string? FindDotEnv(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(Path.Combine(dir.FullName, "Ogasela.slnx")))
            {
                return null;
            }
        }

        return null;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}
