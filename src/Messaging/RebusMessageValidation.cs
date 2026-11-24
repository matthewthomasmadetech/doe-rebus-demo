using System.ComponentModel.DataAnnotations;
using Rebus.Config;
using Rebus.Exceptions;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Pipeline.Receive;
using RebusDemo.Contracts;

namespace RebusDemo.Messaging;

/// <summary>Runs the shared contract rules on incoming application messages.</summary>
public static class RebusMessageValidation
{
    public static void ValidateMessageContracts(this OptionsConfigurer options) =>
        options.Decorate<IPipeline>(context => new PipelineStepInjector(context.Get<IPipeline>())
            .OnReceive(new ValidateContractStep(), PipelineRelativePosition.Before, typeof(ActivateHandlersStep)));

    private sealed class ValidateContractStep : IIncomingStep
    {
        public Task Process(IncomingStepContext context, Func<Task> next)
        {
            // A second-level failure arrives as IFailed<T> and proceeds to its error handler.
            if (context.Load<Message>().Body is IValidatedMessage message)
            {
                var errors = new List<ValidationResult>();
                if (!Validator.TryValidateObject(message, new ValidationContext(message), errors, validateAllProperties: true))
                    throw new InvalidMessageContractException($"{message.GetType().Name} failed validation. " +
                        string.Join(" ", errors.Select(error => error.ErrorMessage)));
            }

            return next();
        }
    }

    // The same payload would fail validation on every delivery, so use Rebus's fail-fast handling.
    private sealed class InvalidMessageContractException(string message) : Exception(message), IFailFastException
    {
    }
}
