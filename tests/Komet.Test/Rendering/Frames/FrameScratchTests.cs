using Komet.Rendering;

namespace Komet.Test.Rendering;

// The two per-frame bodies are patched, and GlTranslate moves the current stack's top as the engine's did
[NonParallelizable]
public sealed class FrameScratchTests
{
    [Test]
    public void GlTranslateMovesTheTopAsTheEngine()
    {
        using var harmony = new TestHarmony("komet-test-framescratch");
        FrameScratch.Install(harmony, null);
        Assert.That(FrameScratch.Patched, Is.EqualTo(2));
        var main = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        (main.MvMatrix, main.PMatrix) = (new StackMatrix4(4), new StackMatrix4(4));
        double[] start = [1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0, 10, 11, 12, 1];
        main.MvMatrix.Push(start);
        main.PMatrix.Push(start);
        main.CurrentMatrixModeProjection = true;
        main.GlTranslate(0.5, -2, 3);
        var expected = Mat4d.Translate(new double[16], start, [0.5, -2, 3]);
        Assert.That(main.PMatrix.Top, Is.EqualTo(expected));
        Assert.That(main.MvMatrix.Top, Is.EqualTo(start));
    }
}
