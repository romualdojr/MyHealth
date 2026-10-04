using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Decides for traces that start in this app (no parent): outgoing calls with nothing around them, such as a background
/// worker's database poll, are not traced; everything else (incoming requests, the app's own activities) is sampled at
/// <paramref name="ratio"/>.
/// </summary>
internal sealed class RequestRootSampler(double ratio) : Sampler
{
    private readonly TraceIdRatioBasedSampler _ratio = new(Math.Clamp(ratio, 0, 1));

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.Kind == ActivityKind.Client
            ? new SamplingResult(SamplingDecision.Drop)
            : _ratio.ShouldSample(samplingParameters);
}
