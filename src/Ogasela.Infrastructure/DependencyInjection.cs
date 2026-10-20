using Ogasela.Application.Analytics;
using Ogasela.Infrastructure.Analytics;
using Amazon;
using Amazon.Rekognition;
using Amazon.Runtime;
using Amazon.S3;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.AdIntegrations;
using Ogasela.Application.Ai.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Messaging.Interfaces;
using Ogasela.Application.Notifications.Interfaces;
using Ogasela.Application.Payments;
using Ogasela.Application.Promotions.Interfaces;
using Ogasela.Application.Listings.Interfaces;
using Ogasela.Application.Marketing.Interfaces;
using Ogasela.Application.Search;
using Ogasela.Application.Staff.Interfaces;
using Ogasela.Application.Verification.Interfaces;
using Ogasela.Infrastructure.Accounts;
using Ogasela.Infrastructure.AdIntegrations;
using Ogasela.Infrastructure.Ai;
using Ogasela.Infrastructure.Common;
using Ogasela.Infrastructure.Listings;
using Ogasela.Infrastructure.Marketing;
using Ogasela.Infrastructure.Messaging;
using Ogasela.Infrastructure.Notifications;
using Ogasela.Infrastructure.Payments;
using Ogasela.Infrastructure.Persistence;
using Ogasela.Infrastructure.Promotions;
using Ogasela.Infrastructure.Reviews;
using Ogasela.Infrastructure.Search;
using Ogasela.Infrastructure.Staff;
using Ogasela.Infrastructure.Verification;
using StackExchange.Redis;

namespace Ogasela.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

        services.AddDbContext<OgaselaDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<OgaselaDbContext>());

        var redisConnectionString = configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("Connection string 'Redis' was not found.");

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
            options.InstanceName = "ogasela:";
        });

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString));

        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));

        services.AddSingleton<IDateTime, SystemDateTime>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<IOtpStore, RedisOtpStore>();
        services.AddScoped<IEngagementTracker, EngagementTracker>();

        services.Configure<SocialAuthSettings>(configuration.GetSection(SocialAuthSettings.SectionName));
        services.AddHttpClient<ISocialTokenVerifier, SocialTokenVerifier>(client => client.Timeout = TimeSpan.FromSeconds(10));

        services.Configure<TermiiSettings>(configuration.GetSection(TermiiSettings.SectionName));
        // The real providers are only used by NotificationDeliveryWorker; handlers get the queued
        // ISmsSender/IEmailSender so a slow provider never holds up a request (see NotificationQueue).
        services.AddHttpClient<TermiiSmsSender>((sp, client) =>
        {
            var baseUrl = configuration[$"{TermiiSettings.SectionName}:BaseUrl"]
                ?? throw new InvalidOperationException("Termii:BaseUrl was not found in configuration.");
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));
        services.AddScoped<SmtpEmailSender>();

        services.AddSingleton<NotificationQueue>();
        services.AddSingleton<ISmsSender, QueuedSmsSender>();
        services.AddSingleton<IEmailSender, QueuedEmailSender>();
        services.AddHostedService<NotificationDeliveryWorker>();

        services.Configure<TurnstileSettings>(configuration.GetSection(TurnstileSettings.SectionName));
        services.AddHttpClient<ITurnstileVerifier, CloudflareTurnstileVerifier>((sp, client) =>
        {
            var baseUrl = configuration[$"{TurnstileSettings.SectionName}:BaseUrl"]
                ?? throw new InvalidOperationException("Turnstile:BaseUrl was not found in configuration.");
            client.BaseAddress = new Uri(baseUrl);
        });

        AddVerification(services, configuration, connectionString);

        services.AddScoped<ICategoryImageStorage, S3CategoryImageStorage>();
        services.AddScoped<IAnnouncementImageStorage, S3AnnouncementImageStorage>();
        services.AddScoped<IProfilePhotoStorage, S3ProfilePhotoStorage>();
        services.AddScoped<IUserDocumentStorage, S3UserDocumentStorage>();
        services.AddScoped<IDisputeEvidenceStorage, S3DisputeEvidenceStorage>();

        services.AddScoped<IPaymentAuthorizer, WalletPaymentAuthorizer>();
        services.AddScoped<ListingExpiryJob>();
        services.AddScoped<ListingExpiringSoonJob>();

        AddPaymentGateways(services, configuration);

        services.AddScoped<IListingSearchRepository, PostgresListingSearchRepository>();

        services.AddSignalR();
        services.AddScoped<IMessagePushNotifier, SignalRMessagePushNotifier>();

        services.AddScoped<RecomputeTrustScoresJob>();

        services.Configure<FirebaseSettings>(configuration.GetSection(FirebaseSettings.SectionName));
        services.AddHttpClient<INotificationChannel, PushNotificationChannel>();
        services.AddScoped<INotificationChannel, EmailNotificationChannel>();

        AddAiTools(services, configuration);

        AddAdIntegrations(services, configuration, redisConnectionString);

        return services;
    }

    private static void AddAdIntegrations(IServiceCollection services, IConfiguration configuration, string redisConnectionString)
    {
        services.Configure<FacebookAdsSettings>(configuration.GetSection(FacebookAdsSettings.SectionName));
        services.Configure<TikTokAdsSettings>(configuration.GetSection(TikTokAdsSettings.SectionName));

        // Token-at-rest encryption for AdAccountConnection uses ASP.NET Core's Data Protection
        // API rather than a separate secrets-manager service (see DataProtectionTokenEncryptor
        // for why) - keys are persisted to Redis (a dedicated, lazily-created connection) instead
        // of local disk, so a token encrypted by one API instance can be decrypted by any other
        // behind the load balancer. The connection is deferred (Lazy<T>, and the Func<IDatabase>
        // overload rather than passing a connected IConnectionMultiplexer directly) so it's only
        // opened the first time a key is actually read/written, not at DI registration time -
        // eagerly connecting here would make `dotnet ef migrations` fail whenever Redis isn't running.
        var lazyDataProtectionRedis = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(redisConnectionString));
        services.AddDataProtection()
            .PersistKeysToStackExchangeRedis(() => lazyDataProtectionRedis.Value.GetDatabase(), "DataProtection-Keys")
            .SetApplicationName("Ogasela");
        services.AddScoped<ITokenEncryptor, DataProtectionTokenEncryptor>();

        // Both are registered as IAdPlatformClient regardless of which platform(s) a given
        // seller has connected - AdPlatformClientFactory picks the right one by AdPlatform at
        // call time (same shape as IPaymentGatewayResolver for IPaymentGateway).
        // AddStandardResilienceHandler wraps each HttpClient with retry/exponential-backoff, a
        // circuit breaker, and a timeout (Microsoft.Extensions.Http.Resilience, built on Polly
        // v8) so a platform rate-limiting or degrading doesn't cascade into the metrics-sync job
        // hammering it further.
        services.AddHttpClient<IAdPlatformClient, FacebookAdsClient>().AddStandardResilienceHandler();
        services.AddHttpClient<IAdPlatformClient, TikTokAdsClient>().AddStandardResilienceHandler();

        services.AddScoped<IAdPlatformClientFactory, AdPlatformClientFactory>();
        services.AddScoped<AdCampaignMetricsSyncJob>();
    }

    private static void AddAiTools(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GeminiSettings>(configuration.GetSection(GeminiSettings.SectionName));
        services.Configure<AnthropicSettings>(configuration.GetSection(AnthropicSettings.SectionName));

        // Ai:Provider picks the active IListingCopyGenerator - "Gemini" (default, currently
        // gemini-2.5-flash-lite) or "Anthropic". Both concrete classes stay in the project
        // either way, so switching back or adding a third provider is a one-line config change,
        // never a code change to anything that depends on IListingCopyGenerator.
        var useAnthropic = string.Equals(configuration["Ai:Provider"], "Anthropic", StringComparison.OrdinalIgnoreCase);

        if (useAnthropic)
        {
            services.AddHttpClient<IListingCopyGenerator, AnthropicListingCopyGenerator>();
        }
        else
        {
            services.AddHttpClient<IListingCopyGenerator, GeminiListingCopyGenerator>();
        }

        services.AddScoped<IPriceSuggestionService, ListingDataPriceSuggestionService>();
        services.AddScoped<IFraudRiskScorer, HeuristicFraudRiskScorer>();
        services.AddScoped<IAiImageStorage, S3AiImageStorage>();
        services.AddScoped<IImageEnhancer, SkiaImageEnhancer>();
    }

    private static void AddPaymentGateways(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PaystackSettings>(configuration.GetSection(PaystackSettings.SectionName));
        services.Configure<FlutterwaveSettings>(configuration.GetSection(FlutterwaveSettings.SectionName));
        services.Configure<PaymentReturnUrlSettings>(configuration.GetSection(PaymentReturnUrlSettings.SectionName));
        services.AddSingleton<IPaymentReturnUrlPolicy, PaymentReturnUrlPolicy>();

        // Both are registered as IPaymentGateway regardless of which is "active" - webhooks
        // are provider-specific by route and must be handled no matter which gateway
        // Payments:Provider currently points new checkouts at (see IPaymentGatewayResolver).
        services.AddHttpClient<IPaymentGateway, PaystackGateway>();
        services.AddHttpClient<IPaymentGateway, FlutterwaveGateway>();
    }

    private static void AddVerification(IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.Configure<AwsSettings>(configuration.GetSection(AwsSettings.SectionName));
        services.Configure<RekognitionSettings>(configuration.GetSection(RekognitionSettings.SectionName));
        services.Configure<StaffKycSettings>(configuration.GetSection(StaffKycSettings.SectionName));

        services.AddSingleton<IAmazonS3>(sp =>
        {
            var aws = sp.GetRequiredService<IOptions<AwsSettings>>().Value;
            var credentials = new BasicAWSCredentials(aws.AccessKeyId, aws.SecretAccessKey);
            return new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(aws.Region));
        });

        services.AddSingleton<IAmazonRekognition>(sp =>
        {
            var aws = sp.GetRequiredService<IOptions<AwsSettings>>().Value;
            var credentials = new BasicAWSCredentials(aws.AccessKeyId, aws.SecretAccessKey);
            return new AmazonRekognitionClient(credentials, RegionEndpoint.GetBySystemName(aws.Region));
        });

        // "Mock" (the default for local/dev) never calls AWS for either face verification or
        // image storage, so the whole flow works with no AWS credentials configured at all.
        var useAws = string.Equals(configuration["Verification:Provider"], "Aws", StringComparison.OrdinalIgnoreCase);

        if (useAws)
        {
            services.AddScoped<IFaceVerificationProvider, AwsRekognitionFaceProvider>();
            services.AddScoped<IBiometricImageStorage, S3BiometricImageStorage>();
            services.AddScoped<IStaffKycDocumentStorage, S3StaffKycDocumentStorage>();
            services.AddScoped<IListingImageStorage, S3ListingImageStorage>();
        }
        else
        {
            services.AddScoped<IFaceVerificationProvider, MockFaceVerificationProvider>();
            services.AddSingleton<IBiometricImageStorage, InMemoryBiometricImageStorage>();
            services.AddSingleton<IStaffKycDocumentStorage, InMemoryStaffKycDocumentStorage>();
            services.AddSingleton<IListingImageStorage, InMemoryListingImageStorage>();
        }

        services.AddScoped<IListingImageProcessor, SkiaListingImageProcessor>();
        services.AddScoped<IBiometricDataErasureService, BiometricDataErasureService>();
        services.AddScoped<RawImageExpiryJob>();

        services.AddHangfire(hangfireConfig => hangfireConfig
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));

        services.AddHangfireServer();
    }
}
