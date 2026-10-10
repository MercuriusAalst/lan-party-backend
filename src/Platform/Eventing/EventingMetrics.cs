using System.Diagnostics.Metrics;

namespace Platform.Eventing;

internal static class EventingMetrics
{
    public const string MeterName = "Mercurius.Eventing";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> DeadLetteredMessages = Meter.CreateCounter<long>(
        "mercurius.outbox.dead_lettered",
        description: "Outbox messages dead-lettered after exhausting their dispatch attempts.");
}
