using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ogasela.Application.Accounts;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Ai;
using Ogasela.Application.Common;
using Ogasela.Application.Common.Behaviours;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Application.Listings;
using Ogasela.Application.Payments;
using Ogasela.Application.Verification;
using Ogasela.Application.Verification.Interfaces;

namespace Ogasela.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(assembly);
            configuration.AddOpenBehavior(typeof(ValidationBehaviour<,>));
        });

        services.AddValidatorsFromAssembly(assembly);

        services.AddScoped<AuthTokenIssuer>();
        services.AddScoped<IOtpService, OtpService>();

        services.Configure<VerificationSettings>(configuration.GetSection(VerificationSettings.SectionName));
        services.AddScoped<IVerificationGuard, VerificationGuard>();

        services.AddScoped<IAuditLogger, AuditLogger>();

        services.Configure<ListingSettings>(configuration.GetSection(ListingSettings.SectionName));
        services.AddScoped<ListingPublishService>();
        services.AddScoped<Listings.Checkout.ListingPaymentSettlement>();
        services.AddScoped<OfficePickups.OfficePickupNotifier>();
        services.AddScoped<OfficePickups.OfficePickupPaymentSettlement>();
        services.AddScoped<Settings.IPlatformSettings, Settings.PlatformSettingsService>();

        services.Configure<PaymentSettings>(configuration.GetSection(PaymentSettings.SectionName));
        services.AddScoped<IPaymentGatewayResolver, PaymentGatewayResolver>();

        services.Configure<AiSettings>(configuration.GetSection(AiSettings.SectionName));
        services.Configure<OtpSettings>(configuration.GetSection(OtpSettings.SectionName));
        services.AddScoped<AiAccessGuard>();

        return services;
    }
}
