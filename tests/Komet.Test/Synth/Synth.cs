using System.Text.Json;

namespace Komet.Test.Synth;

// The synthetic bench: one feature's own workload, run with the feature off (the engine as is) and on (FeatureRig), in
// ABBA rounds inside one process, without the game. It answers whether the feature still earns its code: time per op,
// bytes per op, gen0 collections. Each scenario is a [Category("Synth")] test; scripts/synth.sh runs them one by one and
// prints the table. The tests never fail on a verdict, only when the two arms answer differently (Parity).
internal static class Synth
{
    public const string Category = "Synth";
    private const int Rounds = 6, MinMs = 120;

    public sealed record Result(string Feature, string Scenario, double OffNs, double OnNs, double OffBytes, double OnBytes,
        double TimeDelta, double BytesDelta, double Noise, string Verdict);

    // work runs `ops` operations; probe (optional) gives a value both arms must agree on
    public static Result Run(string feature, string scenario, Action<int> work, int ops = 1000,
        Func<object?>? probe = null, Action? reset = null)
    {
        // Patch once before the off arm runs: MonoMod marks every patched original not-inlineable and the mark outlives the
        // unpatch, so no caller tiered up by the off arm carries an engine body the later patch would never reach
        new FeatureRig(feature).Dispose();
        object? expected = null;
        if (probe is not null)
        {
            reset?.Invoke();
            work(ops);
            expected = probe();
        }

        var batch = Calibrate(work, ops, reset);
        var (off, on) = (new List<(double Ns, double Bytes)>(), new List<(double Ns, double Bytes)>());
        for (var round = 0; round < Rounds; round++)
        {
            var onFirst = round % 4 is 1 or 2; // A B B A A B B A ...
            for (var arm = 0; arm < 2; arm++)
            {
                var enabled = onFirst ? arm == 0 : arm == 1;
                var sample = enabled ? WithFeature(feature, () => Measure(work, batch, reset)) : Measure(work, batch, reset);
                (enabled ? on : off).Add(sample);
                if (probe is null || !enabled) continue;
                var got = WithFeature(feature, () =>
                {
                    reset?.Invoke();
                    work(ops);
                    return probe();
                });
                Assert.That(Json(got), Is.EqualTo(Json(expected)), $"{feature}: the arms answer differently");
            }
        }

        return Report(feature, scenario, off, on);
    }

    private static T WithFeature<T>(string feature, Func<T> body)
    {
        using var rig = new FeatureRig(feature);
        return body();
    }

    // As many ops as take about MinMs, warmed (tier-1 JIT) first
    private static int Calibrate(Action<int> work, int ops, Action? reset)
    {
        for (var i = 0; i < 3; i++)
        {
            reset?.Invoke();
            work(ops);
        }

        var watch = Stopwatch.StartNew();
        reset?.Invoke();
        work(ops);
        var ms = Math.Max(watch.Elapsed.TotalMilliseconds, 0.01);
        return (int)Math.Clamp(ops * MinMs / ms, ops, ops * 1000.0);
    }

    private static (double Ns, double Bytes) Measure(Action<int> work, int ops, Action? reset)
    {
        reset?.Invoke();
        work(Math.Max(1, ops / 10)); // after a patch the method is new: warm it
        GC.Collect();
        GC.WaitForPendingFinalizers();
        reset?.Invoke();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        work(ops);
        var ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        return (ns / ops, (GC.GetAllocatedBytesForCurrentThread() - bytes) / (double)ops);
    }

    private static Result Report(string feature, string scenario, List<(double Ns, double Bytes)> off, List<(double Ns, double Bytes)> on)
    {
        var (offNs, onNs) = (Median(off.Select(s => s.Ns)), Median(on.Select(s => s.Ns)));
        var (offBytes, onBytes) = (Median(off.Select(s => s.Bytes)), Median(on.Select(s => s.Bytes)));
        var time = offNs > 0 ? (onNs - offNs) / offNs : 0;
        var bytesDelta = offBytes > 0 ? (onBytes - offBytes) / offBytes : 0;
        // the spread of the off arm's rounds: a time gain inside it is noise
        var noise = offNs > 0 ? (off.Max(s => s.Ns) - off.Min(s => s.Ns)) / offNs : 0;
        var verdict = "none";
        if ((time <= -0.05 && -time > noise) || (bytesDelta <= -0.10 && offBytes - onBytes >= 8)) verdict = "gain";
        else if (time >= 0.05 && time > noise) verdict = "slower";
        var result = new Result(feature, scenario, offNs, onNs, offBytes, onBytes, time, bytesDelta, noise, verdict);
        TestContext.Out.WriteLine($"synth {feature}/{scenario}: {offNs:F1} -> {onNs:F1} ns/op ({time:+0.0%;-0.0%}), " +
                                  $"{offBytes:F0} -> {onBytes:F0} B/op ({bytesDelta:+0.0%;-0.0%}), noise {noise:0.0%}: {verdict}");
        if (Environment.GetEnvironmentVariable("KOMET_SYNTH_OUT") is { Length: > 0 } path)
            File.AppendAllText(path, JsonSerializer.Serialize(result) + "\n");
        return result;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    private static string Json(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return value?.ToString() ?? "null";
        }
    }
}
