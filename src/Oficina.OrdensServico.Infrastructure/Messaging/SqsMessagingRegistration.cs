using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

public static class SqsMessagingRegistration
{
    public static IServiceCollection AddOrdensMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SqsMessagingOptions>(configuration.GetSection("Messaging:Sqs"));
        var options = configuration.GetSection("Messaging:Sqs").Get<SqsMessagingOptions>() ?? new();
        if (!options.Enabled)
            return services;

        services.AddSingleton<IAmazonSQS>(_ =>
        {
            var config = new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region) };
            if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
                config.ServiceURL = options.ServiceUrl;
            return new AmazonSQSClient(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        });
        services.AddHostedService<OrdensSqsReceiver>();
        services.AddHostedService<OrdensInboxProcessor>();
        services.AddHostedService<OrdensOutboxDispatcher>();
        return services;
    }
}
