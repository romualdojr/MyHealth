namespace MyHealth.Domain.Abstractions;

/// <summary>Input validation failure with per-field messages.</summary>
public sealed record ValidationError(IReadOnlyDictionary<string, string[]> Errors)
    : Error("Validation.Failed", "One or more validation errors occurred.", ErrorType.Validation);
