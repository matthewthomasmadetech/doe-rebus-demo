using System.ComponentModel.DataAnnotations;

namespace RebusDemo.Contracts;

/// <summary>Identifies a message whose validation rules run when a service receives it.</summary>
public interface IValidatedMessage
{
}

/// <summary>Requires an identifier or timestamp to have a non-default value.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequiredValueAttribute() : ValidationAttribute("{0} must have a non-default value.")
{
    public override bool IsValid(object? value) => value switch
    {
        Guid id => id != Guid.Empty,
        DateTimeOffset timestamp => timestamp != default,
        _ => false
    };
}

/// <summary>Values accepted for the optional learning-plan preference.</summary>
public static class LearningPlanFormats
{
    public const string Online = "online";
    public const string Workbook = "workbook";
    public const string ValidationMessage = "PreferredFormat must be online or workbook.";
}
