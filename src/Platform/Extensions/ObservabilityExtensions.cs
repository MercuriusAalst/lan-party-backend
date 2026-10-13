using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Platform.Eventing;

namespace Platform.Extensions;

public static class ObservabilityExtensions
{
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
            builder.Logging.AddJsonConsole();

        // Telemetry is opt-in: without an OTLP endpoint there is nowhere to send it.
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            return builder;

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql", EventingMetrics.MeterName))
            .WithLogging()
            .UseOtlpExporter();

        return builder;
    }
}
