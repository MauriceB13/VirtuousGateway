using VirtuousGateway.Api.Configuration;
using VirtuousGateway.Core.Security;
using VirtuousGateway.Infrastructure.Security;
using VirtuousGateway.Core.Authentication;
using VirtuousGateway.Infrastructure.Authentication;
using VirtuousGateway.Core.Configuration;
using VirtuousGateway.Core.Aplos;
using VirtuousGateway.Infrastructure.Aplos;
using VirtuousGateway.Core.Transactions;
using VirtuousGateway.Infrastructure.Transactions;
using VirtuousGateway.Core.Virtuous;
using VirtuousGateway.Infrastructure.Virtuous;
using Microsoft.Extensions.Options;

namespace VirtuousGateway.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddControllers();

        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen();

        services.Configure<GatewayOptions>(
            configuration.GetSection(GatewayOptions.SectionName));

        services
    .AddOptions<SecurityOptions>()
    .Bind(
        configuration.GetSection(
            SecurityOptions.SectionName))
    .Validate(
        options =>
            !string.IsNullOrWhiteSpace(
                options.ApiKey),
        "Security:ApiKey must be configured.");

        services.Configure<AplosOptions>(
            configuration.GetSection(AplosOptions.SectionName));

        services
            .AddOptions<VirtuousOptions>()
            .Bind(
                configuration.GetSection(
                    VirtuousOptions.SectionName))
            .Validate(
                options => options.OrganizationId > 0,
                "Virtuous:OrganizationId must be greater than zero.")
            .ValidateOnStart();   

        services.AddMemoryCache();

        services.AddSingleton<IAplosTokenDecryptor, RsaAplosTokenDecryptor>();

        services.AddSingleton(
            provider =>
                provider
                    .GetRequiredService<
                        IOptions<VirtuousOptions>>()
                    .Value);

        services.AddSingleton<
            IVirtuousWebhookMapper,
            VirtuousWebhookMapper>();

        services.AddHttpClient<
    IAplosAuthenticationService,
    AplosAuthenticationService>();

    services.AddHttpClient<
    IAplosApiClient,
    AplosApiClient>();

    services
        .AddHttpClient(
            AplosApiClient.TransactionPostClientName)
        .ConfigurePrimaryHttpMessageHandler(
            () =>
                new HttpClientHandler
                {
                    AllowAutoRedirect = false
                });

    services.AddScoped<
    IAplosTransactionService,
    AplosTransactionService>();

    services.Configure<ProcessingLedgerOptions>(
    configuration.GetSection(
       ProcessingLedgerOptions.SectionName));

    services.AddSingleton<
    IVirtuousGiftProcessingLedger,
    PostgresVirtuousGiftProcessingLedger>();

    services.AddSingleton<
    IVirtuousGiftTransactionMapper,
    VirtuousGiftTransactionMapper>();

    services.AddScoped<
    IVirtuousGiftService,
    VirtuousGiftService>();

    services.AddSingleton<
    AplosTransactionResponseParser>();

    services
    .AddOptions<TransactionMappingOptions>()
    .Bind(
        configuration.GetSection(
            TransactionMappingOptions.SectionName))
    .Validate(
        options =>
            options.DepositAccountNumber > 0,
        "TransactionMapping:DepositAccountNumber must be greater than zero.")
    .Validate(
        options =>
            options.IncomeAccountNumber > 0,
        "TransactionMapping:IncomeAccountNumber must be greater than zero.")
    .Validate(
        options =>
            options.FundId > 0,
        "TransactionMapping:FundId must be greater than zero.")
    .ValidateOnStart();

services.AddSingleton(
    provider =>
        provider
            .GetRequiredService<
                IOptions<TransactionMappingOptions>>()
            .Value);

        return services;
    }
}