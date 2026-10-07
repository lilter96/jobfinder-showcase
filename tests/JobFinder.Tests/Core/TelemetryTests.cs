using System.Diagnostics.Metrics;
using JobFinder.Core.Telemetry;

namespace JobFinder.Tests.Core;

public sealed class TelemetryTests
{
    [Fact]
    public void A_stage_records_its_duration_with_scope_and_stage()
    {
        var seen = new List<(double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Telemetry.Name && instrument.Name == "jobfinder.stage.duration") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            seen.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        using (Telemetry.StartStage("answer", "hr").Tag("question.id", "conflict")) { }

        var (duration, recorded) = Assert.Single(seen, s => Equals(s.Tags.GetValueOrDefault("scope"), "hr"));
        Assert.Equal("answer", recorded["stage"]);
        Assert.True(duration >= 0);
    }
}
