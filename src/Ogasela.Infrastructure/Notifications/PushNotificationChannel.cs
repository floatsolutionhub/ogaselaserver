using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Notifications;
using Ogasela.Application.Notifications.Interfaces;
using Ogasela.Domain.Notifications;

namespace Ogasela.Infrastructure.Notifications;

/// <summary>
/// Delivers push notifications via Firebase Cloud Messaging's HTTP v1 API
/// (https://firebase.google.com/docs/cloud-messaging/send-message), authenticated with a
/// service-account OAuth2 token rather than the deprecated legacy server-key API.
/// Silently no-ops (returns false, logged at Information) for a user with no registered
/// PushToken, or when Firebase isn't configured - neither is treated as an error.
/// </summary>
public sealed class PushNotificationChannel : INotificationChannel
{
    private static readonly string[] Scopes = ["https://www.googleapis.com/auth/firebase.messaging"];

    private readonly HttpClient _httpClient;
    private readonly IApplicationDbContext _dbContext;
    private readonly FirebaseSettings _settings;
    private readonly ILogger<PushNotificationChannel> _logger;

    public PushNotificationChannel(
        HttpClient httpClient, IApplicationDbContext dbContext, IOptions<FirebaseSettings> settings,
        ILogger<PushNotificationChannel> logger)
    {
        _httpClient = httpClient;
        _dbContext = dbContext;
        _settings = settings.Value;
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Push;

    public async Task<bool> SendAsync(Guid userId, string type, string payload, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (string.IsNullOrWhiteSpace(user?.PushToken))
        {
            _logger.LogInformation("Skipping push notification {Type} for user {UserId} - no push token registered", type, userId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settings.ProjectId) || string.IsNullOrWhiteSpace(_settings.ServiceAccountJson))
        {
            _logger.LogWarning("Firebase is not configured; skipping push notification {Type} for user {UserId}", type, userId);
            return false;
        }

        try
        {
            var accessToken = await GetAccessTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{_settings.ProjectId}/messages:send")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
                Content = JsonContent.Create(new
                {
                    message = new
                    {
                        token = user.PushToken,
                        notification = BuildNotification(type, payload),
                        data = new Dictionary<string, string> { ["type"] = type, ["payload"] = payload }
                    }
                })
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("FCM send failed with {StatusCode}: {Body}", (int)response.StatusCode, body);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send push notification {Type} to user {UserId}", type, userId);
            return false;
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var serviceAccountCredential = CredentialFactory.FromJson<ServiceAccountCredential>(_settings.ServiceAccountJson);
        var credential = serviceAccountCredential.ToGoogleCredential().CreateScoped(Scopes);
        return await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
    }

    /// <summary>Uses the payload's own "title"/"body" when it carries them; otherwise a generic line for the type.</summary>
    private static object BuildNotification(string type, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                && root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
            {
                return new { title = title.GetString(), body = body.GetString() };
            }
        }
        catch (JsonException)
        {
        }

        return new { title = BuildTitle(type), body = "You have a new notification on Ogasela." };
    }

    private static string BuildTitle(string type) => type switch
    {
        NotificationTypes.ListingExpiringSoon => "Listing expiring soon",
        NotificationTypes.ListingExpired => "Listing expired",
        NotificationTypes.VerificationDecision => "Verification update",
        _ => "Ogasela"
    };
}
