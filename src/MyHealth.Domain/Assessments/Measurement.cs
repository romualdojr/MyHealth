using System.Text.RegularExpressions;
using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Assessments;

/// <summary>
/// One measured value with how it was measured. <see cref="Metric"/>, <see cref="Method"/> and <see cref="Unit"/>
/// together identify what can be compared: a body-fat percentage by bioimpedance is never charted against one by
/// skinfolds (RF-24).
/// </summary>
public sealed partial record Measurement(string Metric, string Method, string Unit, decimal Value)
{
    /// <summary>SI and clinical units accepted by the API (finding 2.1 #10); the apps localise.</summary>
    public static readonly IReadOnlySet<string> Units = new HashSet<string>(StringComparer.Ordinal)
    {
        "kg", "g", "m", "cm", "mm", "%", "bpm", "mmHg", "s", "min", "count", "kg/m2", "ml/kg/min", "kcal", "l", "ml", "points",
    };

    /// <summary>Lower-case codes such as <c>body_mass</c> or <c>skinfold_jackson_pollock_7</c>.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$")]
    private static partial Regex CodePattern();

    public static Result<Measurement> Create(string metric, string method, string unit, decimal value)
    {
        var normalizedMetric = NormalizeCode(metric);
        var normalizedMethod = NormalizeCode(method);
        if (!CodePattern().IsMatch(normalizedMetric) || !CodePattern().IsMatch(normalizedMethod))
        {
            return MeasurementErrors.InvalidCode;
        }

        var normalizedUnit = unit.Trim();
        if (!Units.Contains(normalizedUnit))
        {
            return MeasurementErrors.UnknownUnit(normalizedUnit);
        }

        if (value < 0 || value > 100_000)
        {
            return MeasurementErrors.OutOfRange;
        }

        return new Measurement(normalizedMetric, normalizedMethod, normalizedUnit, value);
    }

    /// <summary>Validates and normalises a set of measurements; the same metric cannot be recorded twice with one method.</summary>
    public static Result<IReadOnlyList<Measurement>> CreateMany(IEnumerable<(string Metric, string Method, string Unit, decimal Value)> values)
    {
        var measurements = new List<Measurement>();
        foreach (var (metric, method, unit, value) in values)
        {
            var measurement = Create(metric, method, unit, value);
            if (measurement.IsFailure)
            {
                return measurement.Error;
            }

            measurements.Add(measurement.Value);
        }

        if (measurements.Select(m => (m.Metric, m.Method)).Distinct().Count() != measurements.Count)
        {
            return MeasurementErrors.Duplicate;
        }

        return measurements;
    }

    private static string NormalizeCode(string code) => code.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
}

public static class MeasurementErrors
{
    public static readonly Error InvalidCode = Error.Validation(
        "Measurement.InvalidCode", "Metric and method must be short codes such as 'body_mass' and 'digital_scale'.");

    public static readonly Error OutOfRange = Error.Validation("Measurement.OutOfRange", "The measured value is out of range.");

    public static readonly Error Duplicate = Error.Validation(
        "Measurement.Duplicate", "Each metric can be recorded only once per method in an assessment.");

    public static Error UnknownUnit(string unit) =>
        Error.Validation("Measurement.UnknownUnit", $"Unknown unit '{unit}'. Use one of: {string.Join(", ", Measurement.Units)}.");
}
