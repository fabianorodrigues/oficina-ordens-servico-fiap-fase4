namespace Oficina.OrdensServico.Infrastructure.Messaging;

public sealed class SqsMessagingOptions
{
    public bool Enabled { get; set; }
    public string ServiceUrl { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string AccessKey { get; set; } = "test";
    public string SecretKey { get; set; } = "test";
    public string EventsQueueName { get; set; } = "oficina-ordens-eventos.fifo";
    public string EventsQueueUrl { get; set; } = string.Empty;
    public string EventsDlqQueueName { get; set; } = "oficina-ordens-eventos-dlq.fifo";
    public string EventsDlqQueueUrl { get; set; } = string.Empty;
    public string CommandsQueueName { get; set; } = "oficina-estoque-comandos.fifo";
    public string CommandsQueueUrl { get; set; } = string.Empty;
    public string CommandsDlqQueueUrl { get; set; } = string.Empty;
    public int ConsumerConcurrency { get; set; } = 1;
    public int MaxMessagesPerReceive { get; set; } = 1;
    public int WaitTimeSeconds { get; set; } = 20;
    public int VisibilityTimeoutSeconds { get; set; } = 60;
}
