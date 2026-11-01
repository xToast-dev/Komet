namespace Komet.Test.Synth;

// Diagnostics: ProfilerPool
[Category(Synth.Category)]
public sealed class DiagnosticsSynth
{
    // One client frame with the profiler on (Komet's HUD): 12 render stages marked with their enum, 20 dialogs' two marks each
    // with their name, 300 plain marks, an entity range with 30 marks of its own: about 376 marks, as a steady-state trace had
    [Test]
    public void ProfilerPool()
    {
        var saved = ScreenManager.FrameProfiler;
        var profiler = new FrameProfilerUtil(static _ => { }) { Enabled = true };
        ScreenManager.FrameProfiler = profiler;
        var stages = Enum.GetValues<EnumRenderStage>().Take(12).ToArray();
        var dialogs = Enumerable.Range(0, 20).Select(i => "dialog" + i).ToArray();
        var plain = Enumerable.Range(0, 300).Select(i => "mark" + i).ToArray();
        var entities = Enumerable.Range(0, 30).Select(i => "entity" + i).ToArray();
        using var regen = new Regen(AccessTools.DeclaredMethod(typeof(FrameProfilerUtil), nameof(FrameProfilerUtil.Mark), [typeof(string)]));
        try
        {
            var result = Synth.Run("ProfilerPool", "frame-376-marks", ops =>
            {
                for (var i = 0; i < ops; i++)
                {
                    SynthCalls.Begin(profiler);
                    for (var s = 0; s < stages.Length; s++)
                    {
                        SynthCalls.Mark(profiler, "beginrenderstage-", stages[s]);
                        for (var m = 0; m < 25; m++) SynthCalls.Mark(profiler, plain[s * 25 + m]);
                    }

                    foreach (var dialog in dialogs)
                    {
                        SynthCalls.Mark(profiler, "gdm-finFr-", dialog);
                        SynthCalls.Mark(profiler, "rendGui", dialog);
                    }

                    SynthCalls.Enter(profiler, "behaviors");
                    foreach (var entity in entities) SynthCalls.Mark(profiler, entity);
                    SynthCalls.Leave(profiler);
                    SynthCalls.End(profiler);
                }
            }, 100, () => profiler.PrevRootEntry.Marks.Count, regen.Again);
            using (new FeatureRig("ProfilerPool"))
                Assert.That(Komet.Diagnostics.ProfilerPool.Patched && Komet.Diagnostics.ProfilerPool.Recycling, Is.True);
            Assert.That(result.Feature, Is.EqualTo("ProfilerPool"));
        }
        finally
        {
            ScreenManager.FrameProfiler = saved;
        }
    }
}
