using System.Globalization;
using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings;
using Ogasela.Application.Notifications;
using Ogasela.Domain.Notifications;
using Ogasela.Domain.Settings;
using Ogasela.Shared;

namespace Ogasela.Application.Settings;

public static class PlatformSettingKeys
{
    public const string RequireListingApproval = "listings.requireApproval";
    public const string FreePlanCap = "listings.freePlanCap";
    public const string MessagingSafetyNotice = "messaging.safetyNotice";

    /// <summary>Platform-wide switch for one channel of one notification category, e.g. "notifications.Verification.Email".</summary>
    public static string NotificationChannel(string category, NotificationChannel channel) => $"notifications.{category}.{channel}";
}

public static class PlatformSettingDefaults
{
    public const string MessagingSafetyNotice = "Do not send money for an item you have not seen and verified";

    public const int MessagingSafetyNoticeMaxLength = 300;
}

/// <summary>Reads the SuperAdmin-editable platform settings, falling back to appsettings when a setting was never changed.</summary>
public interface IPlatformSettings
{
    Task<bool> RequireListingApprovalAsync(CancellationToken cancellationToken);

    Task<int> FreePlanCapAsync(CancellationToken cancellationToken);

    /// <summary>The safety caption shown above the message box in every chat.</summary>
    Task<string> MessagingSafetyNoticeAsync(CancellationToken cancellationToken);

    /// <summary>False when a SuperAdmin has switched this channel off for this category for everyone.</summary>
    Task<bool> IsNotificationChannelEnabledAsync(string category, NotificationChannel channel, CancellationToken cancellationToken);
}

public sealed class PlatformSettingsService : IPlatformSettings
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ListingSettings _listingDefaults;

    public PlatformSettingsService(IApplicationDbContext dbContext, IOptions<ListingSettings> listingDefaults)
    {
        _dbContext = dbContext;
        _listingDefaults = listingDefaults.Value;
    }

    public async Task<bool> RequireListingApprovalAsync(CancellationToken cancellationToken) =>
        bool.TryParse(await ValueAsync(PlatformSettingKeys.RequireListingApproval, cancellationToken), out var value)
            ? value
            : _listingDefaults.RequireApproval;

    public async Task<int> FreePlanCapAsync(CancellationToken cancellationToken) =>
        int.TryParse(await ValueAsync(PlatformSettingKeys.FreePlanCap, cancellationToken), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : _listingDefaults.FreePlanCap;

    public async Task<string> MessagingSafetyNoticeAsync(CancellationToken cancellationToken) =>
        await ValueAsync(PlatformSettingKeys.MessagingSafetyNotice, cancellationToken) is { Length: > 0 } notice
            ? notice
            : PlatformSettingDefaults.MessagingSafetyNotice;

    public async Task<bool> IsNotificationChannelEnabledAsync(
        string category, NotificationChannel channel, CancellationToken cancellationToken) =>
        !bool.TryParse(await ValueAsync(PlatformSettingKeys.NotificationChannel(category, channel), cancellationToken), out var value)
        || value;

    private Task<string?> ValueAsync(string key, CancellationToken cancellationToken) =>
        _dbContext.PlatformSettings.Where(s => s.Key == key).Select(s => (string?)s.Value).FirstOrDefaultAsync(cancellationToken);
}

// ---- Control Portal: Settings (SuperAdmin) ----

public sealed record GetPlatformSettingsQuery : IRequest<Result<PlatformSettingsResponse>>;

public sealed record PlatformSettingsResponse(
    bool RequireListingApproval,
    int FreePlanCap,
    IReadOnlyList<NotificationCategorySettings> Notifications,
    DateTime? UpdatedAt,
    string MessagingSafetyNotice);

public sealed record NotificationCategorySettings(
    string Category, string Label, string Description, IReadOnlyList<NotificationChannelSetting> Channels);

public sealed record NotificationChannelSetting(NotificationChannel Channel, bool Enabled);

/// <summary>Only the parts sent are changed.</summary>
public sealed record UpdatePlatformSettingsCommand(
    bool? RequireListingApproval,
    int? FreePlanCap,
    IReadOnlyList<NotificationSwitch>? Notifications,
    string? MessagingSafetyNotice = null) : IRequest<Result<PlatformSettingsResponse>>;

public sealed record NotificationSwitch(string Category, NotificationChannel Channel, bool Enabled);

public sealed class UpdatePlatformSettingsCommandValidator : AbstractValidator<UpdatePlatformSettingsCommand>
{
    public UpdatePlatformSettingsCommandValidator()
    {
        RuleFor(x => x.FreePlanCap).InclusiveBetween(0, 100).When(x => x.FreePlanCap is not null)
            .WithMessage("Free ads per seller must be between 0 and 100.");
        RuleFor(x => x.MessagingSafetyNotice)
            .Must(n => !string.IsNullOrWhiteSpace(n)).When(x => x.MessagingSafetyNotice is not null)
            .WithMessage("The chat safety notice can't be empty.")
            .MaximumLength(PlatformSettingDefaults.MessagingSafetyNoticeMaxLength)
            .WithMessage($"Keep the chat safety notice under {PlatformSettingDefaults.MessagingSafetyNoticeMaxLength} characters.");
        RuleForEach(x => x.Notifications).ChildRules(s =>
        {
            s.RuleFor(n => n.Category).Must(c => PlatformSettingsHandlers.Categories.Any(k => k.Key == c))
                .WithMessage("Unknown notification category.");
            s.RuleFor(n => n.Channel).Must(c => PlatformSettingsHandlers.Channels.Contains(c))
                .WithMessage("That channel isn't available.");
        });
    }
}

public sealed class PlatformSettingsHandlers :
    IRequestHandler<GetPlatformSettingsQuery, Result<PlatformSettingsResponse>>,
    IRequestHandler<UpdatePlatformSettingsCommand, Result<PlatformSettingsResponse>>
{
    /// <summary>The categories NotificationDispatcher sends, with the wording the portal shows.</summary>
    public static readonly (string Key, string Label, string Description)[] Categories =
    [
        (NotificationCategories.ListingLifecycle, "Ad updates", "Ad approved or rejected, expiring soon, and expired."),
        (NotificationCategories.Verification, "Seller verification", "The result of a seller's identity verification."),
    ];

    /// <summary>Delivery channels that are actually wired up (see INotificationChannel implementations).</summary>
    public static readonly NotificationChannel[] Channels = [NotificationChannel.Push, NotificationChannel.Email];

    private readonly IApplicationDbContext _dbContext;
    private readonly IPlatformSettings _settings;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogger _auditLogger;
    private readonly IDateTime _dateTime;

    public PlatformSettingsHandlers(
        IApplicationDbContext dbContext, IPlatformSettings settings, ICurrentUserService currentUser,
        IAuditLogger auditLogger, IDateTime dateTime)
    {
        _dbContext = dbContext;
        _settings = settings;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
        _dateTime = dateTime;
    }

    public async Task<Result<PlatformSettingsResponse>> Handle(GetPlatformSettingsQuery request, CancellationToken cancellationToken) =>
        Result.Success(await BuildAsync(cancellationToken));

    public async Task<Result<PlatformSettingsResponse>> Handle(UpdatePlatformSettingsCommand request, CancellationToken cancellationToken)
    {
        var changes = new Dictionary<string, string>();
        if (request.RequireListingApproval is { } requireApproval)
        {
            changes[PlatformSettingKeys.RequireListingApproval] = requireApproval ? "true" : "false";
        }

        if (request.FreePlanCap is { } cap)
        {
            changes[PlatformSettingKeys.FreePlanCap] = cap.ToString(CultureInfo.InvariantCulture);
        }

        if (request.MessagingSafetyNotice is { } notice)
        {
            changes[PlatformSettingKeys.MessagingSafetyNotice] = notice.Trim();
        }

        foreach (var notification in request.Notifications ?? [])
        {
            changes[PlatformSettingKeys.NotificationChannel(notification.Category, notification.Channel)] =
                notification.Enabled ? "true" : "false";
        }

        if (changes.Count == 0)
        {
            return Result.Success(await BuildAsync(cancellationToken));
        }

        var keys = changes.Keys.ToList();
        var existing = await _dbContext.PlatformSettings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, cancellationToken);
        var before = existing.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        var now = _dateTime.UtcNow;
        var me = _currentUser.UserId;
        foreach (var (key, value) in changes)
        {
            if (existing.TryGetValue(key, out var setting))
            {
                setting.Set(value, me, now);
            }
            else
            {
                _dbContext.PlatformSettings.Add(PlatformSetting.Create(key, value, me, now));
            }
        }

        await _auditLogger.LogAsync(
            me!.Value, "PlatformSettings.Updated", nameof(PlatformSetting), Guid.Empty,
            JsonSerializer.Serialize(before), JsonSerializer.Serialize(changes), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(await BuildAsync(cancellationToken));
    }

    private async Task<PlatformSettingsResponse> BuildAsync(CancellationToken cancellationToken)
    {
        var notifications = new List<NotificationCategorySettings>();
        foreach (var (key, label, description) in Categories)
        {
            var channels = new List<NotificationChannelSetting>();
            foreach (var channel in Channels)
            {
                channels.Add(new NotificationChannelSetting(
                    channel, await _settings.IsNotificationChannelEnabledAsync(key, channel, cancellationToken)));
            }

            notifications.Add(new NotificationCategorySettings(key, label, description, channels));
        }

        var updatedAt = await _dbContext.PlatformSettings.MaxAsync(s => (DateTime?)s.UpdatedAt, cancellationToken);

        return new PlatformSettingsResponse(
            await _settings.RequireListingApprovalAsync(cancellationToken),
            await _settings.FreePlanCapAsync(cancellationToken),
            notifications,
            updatedAt,
            await _settings.MessagingSafetyNoticeAsync(cancellationToken));
    }
}
