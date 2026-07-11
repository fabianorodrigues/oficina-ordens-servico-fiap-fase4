using Amazon.SQS;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

internal static class QueueUrlResolver
{
    public static async Task<string> Resolve(IAmazonSQS sqs, SqsMessagingOptions options, string queueName, CancellationToken ct)
    {
        if (queueName == options.EventsQueueName && !string.IsNullOrWhiteSpace(options.EventsQueueUrl))
            return options.EventsQueueUrl;
        if (queueName == options.EventsDlqQueueName && !string.IsNullOrWhiteSpace(options.EventsDlqQueueUrl))
            return options.EventsDlqQueueUrl;
        if (queueName == options.CommandsQueueName && !string.IsNullOrWhiteSpace(options.CommandsQueueUrl))
            return options.CommandsQueueUrl;

        var queueUrl = (await sqs.GetQueueUrlAsync(queueName, ct)).QueueUrl;
        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
            return queueUrl;

        var service = new Uri(options.ServiceUrl);
        var original = new Uri(queueUrl);
        return new UriBuilder(original)
        {
            Scheme = service.Scheme,
            Host = service.Host,
            Port = service.Port
        }.Uri.ToString();
    }
}
