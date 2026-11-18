using Komet.Options;
using Komet.Rendering;

namespace Komet.Test.Options;

[NonParallelizable]
public sealed class ShaderOverviewLifecycleTests
{
    [Test]
    public void AClosedWorldsQueuedRefreshDoesNotTouchTheNextWorld()
    {
        var queued = new List<Action>();
        var events = Answers.Of<IClientEventAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["EnqueueMainThreadTask"] = args =>
            {
                queued.Add((Action)args![0]!);
                return null;
            }
        });
        var world = Answers.Of<IClientWorldAccessor>([]);
        var api = Answers.Of<ICoreClientAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
            { ["get_Event"] = _ => events, ["get_World"] = _ => world });
        var saved = AccessTools.Field(typeof(ShaderState), "_saved");
        var original = saved.GetValue(null);
        try
        {
            saved.SetValue(null, new ShaderState.Saved());
            ShaderOverview.Hook(api);
            _ = AccessTools.Method(typeof(ShaderOverview), "Ask").Invoke(null, null);
            ShaderOverview.Unhook();
            saved.SetValue(null, new ShaderState.Saved());
            ShaderOverview.Hook(api);
            _ = AccessTools.Method(typeof(ShaderOverview), "Ask").Invoke(null, null);
            Assert.That(queued, Has.Count.EqualTo(2));
            queued[0]();
            Assert.That(AccessTools.Field(typeof(ShaderOverview), "_asked").GetValue(null), Is.True);
        }
        finally
        {
            ShaderOverview.Unhook();
            saved.SetValue(null, original);
        }
    }
}
