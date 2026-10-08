namespace Komet.Vulkan;

internal sealed partial class SceneRenderer
{
    private const uint Texture2D = 0x0DE1;
    private const int MaxSeen = 256;

    // What the units of a program's samplers hold: the texture of each sampler's kind and the sampler object, up to four
    private readonly record struct Units(uint T0, uint S0, uint T1, uint S1, uint T2, uint S2, uint T3, uint S3)
    {
        public const int Max = 4;
    }

    private sealed record Seen(TerrainDraw.Program Recorded, TerrainDraw.Texture[] Resolved, SharedImage[] Images,
        (int Unit, uint Kind, uint Texture, int Sampler)[] Bound);

    private TerrainDraw.Program? Sampled(Drawn program, VulkanFrame.Target target, out string why)
    {
        why = "";
        _sampled.Clear();
        if (!NotNull(program) || !NotNull(target)) return null;
        var key = KeyOf(program, target);
        if (program.Recorded is { } fast && program.Key == key)
        {
            _sampled.AddRange(program.Images); // no binding, sampler, copy or shared image changed since
            return fast;
        }

        var units = UnitsOf(program);
        if (units is { } bound && Known(program, target, bound) is { } seen) return Recalled(program, (target, key), seen);
        if (program.Recorded is { } same && Unchanged(program, target))
        {
            program.Key = key;
            _sampled.AddRange(program.Images);
            return same;
        }

        _bound.Clear();
        var recorded = Resolve(program, target, out why);
        (program.Key, program.Held) = recorded is null ? default : (KeyOf(program, target), HeldOf(program, target));
        program.Images = recorded is null ? [] : [.. _sampled];
        program.Bound = recorded is null ? [] : [.. _bound];
        if (recorded is not null && units is { } resolved) Keep(program, (resolved, target), recorded);
        return recorded;
    }

    private TerrainDraw.Program Recalled(Drawn program,
        (VulkanFrame.Target Target, (long, int, long, long, VulkanFrame.Target?) Key) at, Seen seen)
    {
        (program.Key, program.Held, program.Recorded) = (at.Key, HeldOf(program, at.Target), seen.Recorded);
        (program.Images, program.Bound) = (seen.Images, seen.Bound);
        if (program.Resolved.Length != seen.Resolved.Length) program.Resolved = new TerrainDraw.Texture[seen.Resolved.Length];
        seen.Resolved.CopyTo(program.Resolved, 0);
        _sampled.AddRange(seen.Images);
        _ = Assert(_sampled.Count <= MaxSamplers) && NotNull(seen.Recorded);
        return seen.Recorded;
    }

    // The units of at most four samplers: what each holds, the texture of the sampler's kind and the sampler object
    private static Units? UnitsOf(Drawn program)
    {
        var count = program.Kinds.Length;
        if (!Assert(count <= MaxSamplers) || count > Units.Max) return null;
        Span<uint> held = stackalloc uint[2 * Units.Max];
        held.Clear();
        for (var i = 0; i < Math.Min(count, Units.Max); i++)
        {
            var unit = program.Program.Unit(i);
            (held[2 * i], held[2 * i + 1]) = (GlTap.UnitTexture(unit, program.Kinds[i]), (uint)GlTap.Unit(unit).Sampler);
        }

        _ = Assert(held.Length == 8);
        return new Units(held[0], held[1], held[2], held[3], held[4], held[5], held[6], held[7]);
    }

    // Resolved before with the units holding the same, and no texture deleted, sampler uniform set, copy or target changed since
    private Seen? Known(Drawn program, VulkanFrame.Target target, Units units)
    {
        if (!NotNull(program) || !Assert(program.Seen.Count <= MaxSeen)) return null;
        var held = SeenOf(program, target);
        if (program.SeenHeld == held) return _terrain.Copies.AnyDirty ? null : program.Seen.GetValueOrDefault(units);
        program.Seen.Clear();
        program.SeenHeld = held;
        return null;
    }

    // A dirty copy is refreshed by the next resolve that reads it (Copy), so the cache answers only while none is
    private (long, int, long, long, VulkanFrame.Target?) SeenOf(Drawn program, VulkanFrame.Target target) =>
        Assert(target is not null) && NotNull(program)
            ? (GlTap.DeleteVersion, program.Program.Mirror.Samplers, _terrain.Copies.Shape, _terrain.Targets.Version, target)
            : default;

    private void Keep(Drawn program, (Units Units, VulkanFrame.Target Target) at, TerrainDraw.Program recorded)
    {
        if (!NotNull(recorded) || !Assert(program.Seen.Count <= MaxSeen)) return;
        var held = SeenOf(program, at.Target);
        if (program.SeenHeld != held || program.Seen.Count >= MaxSeen)
        {
            program.Seen.Clear();
            program.SeenHeld = held;
        }

        program.Seen[at.Units] = new Seen(recorded, [.. program.Resolved], program.Images, program.Bound);
    }

    private (long, int, long, long, VulkanFrame.Target?) HeldOf(Drawn program, VulkanFrame.Target target) =>
        Assert(target is not null) && NotNull(program)
            ? (GlTap.DeleteVersion, program.Program.Mirror.Samplers, _terrain.Copies.Version, _terrain.Targets.Version,
                target)
            : default;

    // Other binds came between, but every unit the program's samplers read holds what it held when they were resolved
    private bool Unchanged(Drawn program, VulkanFrame.Target target)
    {
        if (!NotNull(program) || program.Held != HeldOf(program, target)) return false;
        foreach (var (unit, kind, texture, sampler) in program.Bound.Bounded(MaxSamplers))
            if (GlTap.UnitTexture(unit, kind) != texture || GlTap.Unit(unit).Sampler != sampler)
                return false;
        return Assert(program.Bound.Length <= MaxSamplers);
    }

    private (long, int, long, long, VulkanFrame.Target?) KeyOf(Drawn program, VulkanFrame.Target target) =>
        Assert(target is not null) && NotNull(program)
            ? (GlTap.TextureVersion, program.Program.Mirror.Samplers, _terrain.Copies.Version, _terrain.Targets.Version,
                target)
            : default;

    private TerrainDraw.Program? Resolve(Drawn program, VulkanFrame.Target target, out string why)
    {
        why = "";
        if (!NotNull(program) || !Assert(_sampled.Count == 0)) return null;
        var samplers = program.Program.Ported.Samplers;
        var count = Math.Min(samplers.Length, MaxSamplers);
        for (var i = 0; i < Math.Min(count, MaxSamplers); i++)
            if (Resolved(program, (samplers[i], i), target, out why) is { } texture) _resolved[i] = texture;
            else return null;
        var resolved = _resolved.AsSpan(0, count);
        if (program.Recorded is { } last && resolved.SequenceEqual(program.Resolved)) return last;
        if (program.Resolved.Length != count) program.Resolved = new TerrainDraw.Texture[count];
        resolved.CopyTo(program.Resolved);
        return program.Recorded = program.Program.Recorded(resolved);
    }

    private TerrainDraw.Texture? Resolved(Drawn program, (GlslPort.Sampler Sampler, int At) port,
        VulkanFrame.Target target, out string why)
    {
        why = "";
        var sampler = port.Sampler;
        if (sampler.Count > 1) return Unsampled(out why, program, sampler.Name, "an array of samplers");
        var unit = program.Program.Unit(port.At);
        var kind = program.Kinds[port.At];
        var texture = (int)GlTap.UnitTexture(unit, kind);
        if (texture == 0) return Blank(program, (sampler, unit, kind), out why);
        var image = _terrain.Targets.Find(texture);
        if (image is null && kind is not (Texture2D or GlTap.TextureCube))
            return Unsampled(out why, program, sampler.Name, "not a 2D texture or a cube map");
        image ??= _terrain.Copy(texture, out why);
        if (image is null) return Unsampled(out why, program, sampler.Name, why);
        if (image.Cube != (kind == GlTap.TextureCube)) return Unsampled(out why, program, sampler.Name, "no cube copy");
        var samplerObject = GlTap.Unit(unit).Sampler;
        _bound.Add((unit, kind, (uint)texture, samplerObject));
        var state = samplerObject > 0
            ? SamplerObject(samplerObject)
            : _terrain.Targets.Sampling(texture) ?? _terrain.Copies.SamplingOf(texture);
        if (state is null) return Unsampled(out why, program, sampler.Name, $"texture {texture} has no sampling");
        if (!_sampled.Contains(image)) _sampled.Add(image);
        var layout = image == target.Depth ? LayoutDepthReadOnly : Vk.LayoutShaderRead;
        return new TerrainDraw.Texture(image.Sampled, _terrain.Sampling.Get(state.Value), layout);
    }

    public System.Func<int, Samplers.State> SamplerObject { get; set; } = _ => default;

    private static string Unblank(int sampler) => sampler switch
    {
        _ when GlTap.DefaultTextureTouched => "the engine bound no texture to it, and texture 0 was set up",
        0 => "the engine bound no texture to it",
        _ => Assert(sampler > 0) ? "the engine bound no texture to it, read through a sampler object" : ""
    };

    // A unit holding no texture: what OpenGL reads from texture 0, nothing the frame hands over
    private TerrainDraw.Texture? Blank(Drawn program, (GlslPort.Sampler Sampler, int Unit, uint Kind) at, out string why)
    {
        why = "";
        var sampler = GlTap.Unit(at.Unit).Sampler;
        if (_terrain.Unbound(at.Sampler.Type, sampler) is not { } blank)
            return Unsampled(out why, program, at.Sampler.Name, Unblank(sampler));
        _bound.Add((at.Unit, at.Kind, 0, sampler));
        return Assert(at.Kind == Texture2D) ? blank : null;
    }

    private static TerrainDraw.Texture? Unsampled(out string why, Drawn program, string sampler, string reason)
    {
        why = $"{program.Name} reads {sampler}: {reason}";
        _ = Assert(why.Length > 0) && NotNull(program);
        return null;
    }

    private static uint TargetOf(string type)
    {
        _ = Assert(type.Length > 0) && Assert(type.Contains("sampler", StringComparison.Ordinal));
        if (type.Contains("Cube", StringComparison.Ordinal)) return GlTap.TextureCube;
        return type.Contains("Array", StringComparison.Ordinal) ? GlTap.Texture2DArray : Texture2D;
    }

    // The program's last vertex array again, unchanged: no lookup
    private Layout? LayoutFor(GlTap.VertexArray array, Drawn program)
    {
        if (program.Laid is var (last, changes, known) && ReferenceEquals(last, array) && changes == array.Changes)
            return known;
        var layout = LayoutOf(array, program);
        program.Laid = (array, array.Changes, layout);
        _ = Assert(_layouts.Count <= MaxLayouts) && NotNull(array);
        return layout;
    }

    // A disabled attribute reads the constant (0, 0, 0, 1), as OpenGL gives it
    private Layout? LayoutOf(GlTap.VertexArray array, Drawn program)
    {
        if (!NotNull(array) || !NotNull(program)) return null;
        var key = (array, array.Changes, program.Program.Id);
        if (_layouts.TryGetValue(key, out var known)) return known;
        var attributes = new List<TerrainPools.Attribute>();
        var sources = new List<(int, uint, long)>();
        foreach (var input in program.Program.Ported.Attributes.Bounded(MaxInputs))
        {
            var a = input.Location < GlTap.MaxAttributes ? array.Attributes[input.Location] : default;
            var integer = input.Type.StartsWith('i') || input.Type.StartsWith('u');
            if (a.Divisor > 1 || (a.Enabled && Mirrors.Persistent(a.Buffer))) return Remember(key, null);
            var constantType = (int)(integer ? GlInt : GlFloat);
            var made = a.Enabled
                ? new TerrainPools.Attribute(input.Location, sources.Count, a.Size, (int)a.Type, a.Normalized, a.Integer)
                {
                    Stride = a.Stride, Offset = a.Offset, Divisor = (int)a.Divisor
                }
                : new TerrainPools.Attribute(input.Location, sources.Count, 4, constantType, false, integer);
            if (TerrainPipeline.Format(made) == 0) return Remember(key, null);
            attributes.Add(made);
            var constant = integer ? 16L : 0L; // where the constant of the input's kind lies
            // The pointer's offset goes into the binding: the pipeline's attributes start at 0 (interleaved instance data, the
            // particles' position and scale, sits at 0 and 12 in one buffer)
            sources.Add((input.Location, a.Enabled ? a.Buffer : 0, a.Enabled ? a.Offset : constant));
        }

        var text = string.Join(';', attributes.Select(a =>
            $"{a.Location}:{a.Size}:{a.Type}:{a.Normalized}:{a.Integer}:{a.Stride}:{a.Divisor}"));
        if (!_formats.TryGetValue(text, out var formats))
        {
            if (_formats.Count >= MaxLayouts) _formats.Clear();
            _formats[text] = formats = new Formats(text, [.. attributes]);
        }

        return Remember(key, new Layout(formats, [.. sources]));
    }

    private Layout? Remember((GlTap.VertexArray, long, int) key, Layout? layout)
    {
        if (_layouts.Count >= MaxLayouts) _layouts.Clear();
        _layouts[key] = layout;
        _ = Assert(_layouts.Count <= MaxLayouts);
        return layout;
    }

    // waiting: the pipeline is being made, the draw stays OpenGL's (nothing remembered, so a later draw finds it made)
    private TerrainPipeline? Pipeline(Drawn program, Layout layout,
        (uint Topology, VulkanFrame.Target Target, GlTap.Fixed State) pass, out bool waiting)
    {
        waiting = false;
        var (topology, target, state) = pass;
        if (!NotNull(layout) || !Assert(target.Colors.Length <= GlTap.MaxBuffers)) return null;
        var formats = layout.Formats;
        if (program.Last is var (l, t, g, f, p) && ReferenceEquals(l, formats) && t == topology &&
            ReferenceEquals(g, target) && f.Equals(state)) return p; // the program's last draw's, compared, not hashed
        var quick = new QuickKey(program, formats, topology, target, state);
        if (!_quick.TryGetValue(quick, out var made))
        {
            made = Made(program, formats, pass, out waiting);
            if (waiting) return null;
            if (_quick.Count >= MaxPipelines) _quick.Clear();
            _quick[quick] = made;
        }

        program.Last = (formats, topology, target, state, made);
        return made;
    }

    private TerrainPipeline? Made(Drawn program, Formats formats,
        (uint Topology, VulkanFrame.Target Target, GlTap.Fixed State) pass, out bool waiting)
    {
        waiting = false;
        var (topology, target, state) = pass;
        if (!NotNull(formats) || !Assert(target.Colors.Length <= GlTap.MaxBuffers)) return null;
        for (var i = 0; i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
            if (!TerrainPipeline.Blendable(state.Blend[i]))
                return null;
        var into = _terrain.Formats(target, ref state);
        var key = new PipelineKey(program.Program.Id, program.Port, $"{formats.Key}|{topology}", into.Key, state);
        var (device, attributes) = (_terrain.Device, formats.Attributes);
        return _pipelines.Get(key, (program.Name, program.Program.Ported),
            ported => TerrainPipeline.Started(device, ported, attributes, (state, into.Target, topology)), false, out waiting);
    }

    // The old ports' pipelines go once no frame in flight uses them: no handoff, no wait for the GPU. keep: a port's to keep
    private void ForgetPipelines(int program, int keep)
    {
        if (!Assert(program >= 0) || !Assert(_pipelines.Count <= MaxPipelines)) return;
        _quick.Clear();
        _pipelines.Forget(key => key.Program == program && key.Port != keep);
    }

    private bool Buffers(Drawn program, (GlTap.VertexArray Array, Layout Layout) input, GlTap.DrawCall call,
        out (ulong Buffer, ulong Offset, uint Type)? indices, out string why)
    {
        (indices, why) = (null, "");
        _vertices.Clear();
        if (!NotNull(program) || !NotNull(input.Layout)) return false;
        foreach (var (location, buffer, offset) in input.Layout.Sources.Bounded(MaxInputs))
        {
            var mirror = buffer == 0 ? (Constants.Buffer, 0UL, Constants.Size) : Mirrors.Read(buffer);
            if (mirror is not { } m) return Refuse(out why, $"buffer {buffer} has no mirror");
            _vertices.Add(((uint)location, m.Buffer, m.Offset + (ulong)offset));
        }

        if (call.IndexType != 0)
        {
            if (Mirrors.Read(input.Array.Elements) is not { } e) return Refuse(out why, "the indices have no mirror");
            var type = call.IndexType == UnsignedInt ? Vk.IndexUint32 : Vk.IndexUint16;
            indices = (e.Buffer, e.Offset + (ulong)call.Indices, type);
        }

        return Blocks(program, out why);
    }

    private bool Blocks(Drawn program, out string why)
    {
        why = "";
        _blocks.Clear();
        var blocks = program.Program.Ported.Blocks;
        if (!Assert(program.Points.Length == blocks.Length)) return Refuse(out why, "the blocks were not looked up");
        for (var i = 0; i < Math.Min(blocks.Length, MaxBlocks); i++)
        {
            var point = program.Points[i];
            (uint Buffer, long Offset, long Size) bound = default;
            if (point >= 0) bound = blocks[i].Storage ? GlTap.StorageBinding(point) : GlTap.UniformBinding(point);
            var descriptor = new Vk.BufferDescriptor { Buffer = Constants.Buffer, Range = Constants.Size };
            if (bound.Buffer != 0)
            {
                if (Mirrors.Read(bound.Buffer) is not { } m)
                    return Refuse(out why, $"{program.Name}'s block {blocks[i].Name} reads a buffer with no mirror");
                var size = bound.Size > 0 ? (ulong)bound.Size : m.Size - (ulong)bound.Offset;
                descriptor = new Vk.BufferDescriptor
                {
                    Buffer = m.Buffer, Offset = m.Offset + (ulong)bound.Offset, Range = size
                };
            }

            _blocks.Add(new TerrainDraw.BlockBuffer(blocks[i].Binding, descriptor));
        }

        return true;
    }
}
