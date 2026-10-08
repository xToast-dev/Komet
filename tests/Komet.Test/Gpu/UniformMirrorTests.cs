using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class UniformMirrorTests
{
    [Test]
    public void TheFastWritesPlaceEveryWordAsTheWordByWordOne()
    {
        (string Type, int Stride)[] types =
        [
            ("float", 16), ("int", 16), ("vec2", 16), ("vec3", 16), ("vec4", 16), ("ivec4", 16), ("mat3", 48), ("mat4", 64)
        ];
        var r = new Random(7);
        foreach (var (type, stride) in types)
            foreach (var count in new[] { 1, 3, 40 })
                foreach (var start in new[] { 0, 1, 5 })
                {
                    var u = new GlslPort.Uniform("u", type, count, null, 32, count > 1 ? stride : 0);
                    var source = new uint[r.Next(1, 200)];
                    for (var i = 0; i < source.Length; i++) source[i] = (uint)r.Next();
                    var (fast, each) = (new uint[800], new uint[800]);
                    UniformMirror.Write(u, source, fast, start);
                    UniformMirror.WriteEach(u, source, each, start);
                    Assert.That(fast, Is.EqualTo(each), $"{type}[{count}] from {start}, {source.Length} words");
                }
    }
}
