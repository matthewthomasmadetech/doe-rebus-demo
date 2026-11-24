using System.ComponentModel.DataAnnotations;

namespace RebusDemo.Contracts;

/// <summary>Asks Develop to send a learning plan reminder as a standalone operation.</summary>
public sealed record SendLearnerReminderV1(
    [property: RequiredValue] Guid ReminderId,
    [property: Required, StringLength(80)] string LearnerId) : IValidatedMessage;
