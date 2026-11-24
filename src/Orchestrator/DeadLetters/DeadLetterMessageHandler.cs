using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Retry;
using Rebus.Transport;
using RebusDemo.Contracts;

namespace RebusDemo.Orchestrator.DeadLetters;

/// <summary>Archives raw messages from the shared Rebus error queue.</summary>
internal sealed class DeadLetterMessageHandler(
    DeadLetterStore store, ILogger<DeadLetterMessageHandler> logger, IHostApplicationLifetime lifetime)
    : IIncomingStep, IErrorHandler
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>Persists the envelope before Rebus acknowledges the delivery.</summary>
    public Task Process(IncomingStepContext context, Func<Task> next) =>
        ArchiveAsync(context.Load<TransportMessage>());

    /// <summary>Retries the SQL write on the original delivery with a delay.</summary>
    public async Task HandlePoisonMessage(
        TransportMessage message, ITransactionContext transactionContext, ExceptionInfo exception)
    {
        // Release this Azure queue delivery if the retry fails. Rebus changes the result to acknowledged on success.
        transactionContext.SetResult(commit: false, ack: false);

        logger.LogWarning("Retrying archive of {MessageId} from {Queue} in {DelaySeconds} seconds. {Reason}",
            message.Headers.GetValueOrDefault(Headers.MessageId), QueueNames.Errors, RetryDelay.TotalSeconds,
            exception.Message);

        await Task.Delay(RetryDelay, lifetime.ApplicationStopping);

        await ArchiveAsync(message);
    }

    private async Task ArchiveAsync(TransportMessage message)
    {
        if (await store.SaveAsync(message, lifetime.ApplicationStopping))
        {
            logger.LogInformation("Archived {MessageType} from {SourceQueue}\n    Message {MessageId} | Correlation {CorrelationId}",
                message.Headers.GetValueOrDefault(Headers.Type), message.Headers.GetValueOrDefault(Headers.SourceQueue),
                message.Headers.GetValueOrDefault(Headers.MessageId), message.Headers.GetValueOrDefault(Headers.CorrelationId));
        }
    }
}
