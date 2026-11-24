using System.ComponentModel.DataAnnotations;

namespace RebusDemo.Contracts;

/// <summary>Creates a learner registration with a required preferred name.</summary>
public sealed record CreateLearnerV2(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string FirstName,
    [property: Required, StringLength(80)] string LastName,
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(80)] string PreferredName) : IValidatedMessage;
