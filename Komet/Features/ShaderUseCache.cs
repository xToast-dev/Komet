using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Features;

// Use() re-uploads every per-frame uniform on each call and is called once per entity, held item and block entity.
// The values only change once per frame, so keep the last uploaded copy per program and skip what is unchanged.
internal static class ShaderUseCache
{
    public static bool Enabled { get; set { field = value; if (value) Array.Clear(States); } } = true;
    public static bool Stats { get; set; }
    public static int Calls { get; private set; }
    public static int Uploads { get; private set; }
    public static int Skips { get; private set; }
    public static void ResetStats() => (Calls, Uploads, Skips) = (0, 0, 0);

    private static readonly string[] Includes =
    [
        "fogandlight.fsh", "fogandlight.vsh", "shadowcoords.vsh", "vertexwarp.vsh",
        "skycolor.fsh", "colormap.vsh", "underwatereffects.fsh"
    ];
    private static readonly Vec3f GuiLight = new(0.7071068f, -0.7071068f, 0f);
    private const int MaxSlots = 64, MaxUbos = 16;
    private static readonly State?[] States = new State[128];
    private static int _frame;

    private sealed class State
    {
        public required ShaderProgramBase Program;
        public int ProgramId, ShadowQuality, Mask, Frame = -1;
        public Slot[] Slots = [];
        public float[] Prev = [], Cur = [];
    }

    // Kind = floats per element (1-4 = vecN, 16 = mat4), 0 = int
    private record struct Slot(int Loc, int Kind, int Offset, int Reserved)
    {
        public int Count;
        public bool Dirty;
    }

    private ref struct Writer(State state, ShaderProgramBase program, List<Slot>? layout)
    {
        private int _i;
        public int Total;
        public bool Overflow;
        public readonly List<Slot>? Layout = layout;

        private int Reserve(string name, int kind, int count, int reserve)
        {
            if (!Assert(name.Length > 0) || !Assert(kind is >= 0 and <= 16) || !Assert(count >= 0) || !Assert(reserve >= count * Math.Max(kind, 1))) { Overflow = true; return -1; }
            if (Layout == null)
            {
                if (!Assert(_i < state.Slots.Length)) { Overflow = true; return -1; }
                ref var slot = ref state.Slots[_i++];
                if (count * Math.Max(kind, 1) > slot.Reserved) { Overflow = true; return -1; }
                slot.Dirty = slot.Count != count;
                slot.Count = count;
                return slot.Offset;
            }
            Layout.Add(new Slot(program.uniformLocations.GetValueOrDefault(name, -1), kind, Total, reserve));
            Total += reserve;
            return -1;
        }

        public void F(string name, float v) { if (Reserve(name, 1, 1, 1) is >= 0 and var o) state.Cur[o] = v; }
        public void I(string name, int v) { if (Reserve(name, 0, 1, 1) is >= 0 and var o) state.Cur[o] = BitConverter.Int32BitsToSingle(v); }
        public void V2(string name, float x, float y) { if (Reserve(name, 2, 1, 2) is >= 0 and var o) (state.Cur[o], state.Cur[o + 1]) = (x, y); }
        public void V3(string name, Vec3f v) { if (Reserve(name, 3, 1, 3) is >= 0 and var o) (state.Cur[o], state.Cur[o + 1], state.Cur[o + 2]) = (v.X, v.Y, v.Z); }
        public void V4(string name, Vec4f v) { if (Reserve(name, 4, 1, 4) is >= 0 and var o) (state.Cur[o], state.Cur[o + 1], state.Cur[o + 2], state.Cur[o + 3]) = (v.X, v.Y, v.Z, v.W); }
        public void Arr(string name, int kind, float[] src, int count)
        {
            if (!Assert(kind > 0) || !Assert(count * kind <= src.Length)) { Overflow = true; return; }
            if (Reserve(name, kind, count, src.Length) is >= 0 and var o) Array.Copy(src, 0, state.Cur, o, count * kind);
        }
        public void Tex(string name, int textureId) { if (Assert(name.Length > 0) && Layout == null) program.BindTexture2D(name, textureId); }
    }

    public static void Install(Harmony harmony)
    {
        var update = AccessTools.Method(typeof(DefaultShaderUniforms), nameof(DefaultShaderUniforms.Update));
        var use = AccessTools.Method(typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use));
        if (!NotNull(update) || !NotNull(use)) return;
        if (!NotNull(harmony.Patch(update, postfix: new HarmonyMethod(NextFrame)))) return;
        _ = NotNull(harmony.Patch(use, prefix: new HarmonyMethod(Use)));
    }

    private static void NextFrame() => _frame++;

    // ReSharper disable once InconsistentNaming
    private static bool Use(ShaderProgramBase __instance) => Activate(__instance);

    private static bool Activate(ShaderProgramBase p)
    {
        if (!Enabled || (uint)p.PassId >= (uint)States.Length) return true;
        if (!Assert(p.ProgramId > 0) || !NotNull(p.uniformLocations)) return true;
        if (ShaderProgramBase.CurrentShaderProgram is { } active && active != p)
            throw new InvalidOperationException("Already a different shader (" + active.PassName + ") in use!");
        if (p.Disposed) throw new InvalidOperationException("Can't use a disposed shader!");

        GL.UseProgram(p.ProgramId);
        ShaderProgramBase.CurrentShaderProgram = p;
        if (Stats) Calls++;

        var s = States[p.PassId];
        if (s?.Program != p || s.ProgramId != p.ProgramId || s.ShadowQuality != ShaderProgramBase.shadowmapQuality)
            States[p.PassId] = s = Build(p);

        var w = new Writer(s, p, null);
        Fill(ref w, s.Mask);
        if (w.Overflow) { States[p.PassId] = null; return true; }

        Upload(s, force: s.Frame != _frame);
        s.Frame = _frame;
        if (p.ubos.Count == 0 || !Assert(p.ubos.Count <= MaxUbos)) return false;
        using var ubos = p.ubos.GetEnumerator();
        for (var i = 0; i < MaxUbos && ubos.MoveNext(); i++) ubos.Current.Value.Bind();
        return false;
    }

    private static State Build(ShaderProgramBase p)
    {
        var mask = p == ShaderPrograms.Gui ? 1 << Includes.Length : 0;
        for (var i = 0; i < Includes.Length; i++) if (p.includes.Contains(Includes[i])) mask |= 1 << i;
        var s = new State { Program = p, ProgramId = p.ProgramId, ShadowQuality = ShaderProgramBase.shadowmapQuality, Mask = mask };
        var layout = new List<Slot>();
        var w = new Writer(s, p, layout);
        Fill(ref w, mask);
        if (!Assert(!w.Overflow) || !Assert(w.Total >= layout.Count) || !Assert(layout.Count <= MaxSlots)) return s;   // a slot reserves at least one float
        s.Slots = [.. layout];
        s.Prev = new float[w.Total];
        s.Cur = new float[w.Total];
        return s;
    }

    private static void Fill(ref Writer w, int mask)
    {
        var platform = ScreenManager.Platform;
        var u = platform.ShaderUniforms;
        if (!NotNull(u) || !Index(mask, 256) || !Assert(platform.FrameBuffers.Count > 12)) { w.Overflow = true; return; }
        var viewDistance = (float)ClientSettings.ViewDistance;
        var viewDistanceLod0 = Math.Min(640, ClientSettings.ViewDistance) * ClientSettings.LodBias;
        if ((mask & 1) != 0)
        {
            w.F("zNear", u.ZNear);
            w.F("zFar", u.ZFar);
            w.V3("lightPosition", u.LightPosition3D);
            w.F("shadowIntensity", u.DropShadowIntensity);
            w.F("glitchStrength", u.GlitchStrength);
            w.F("psychedelicStrength", u.PsychedelicStrength);
            if (ShaderProgramBase.shadowmapQuality > 0)
            {
                var far = platform.FrameBuffers[11];
                w.Tex("shadowMapFar", far.DepthTextureId);
                w.Tex("shadowMapNear", platform.FrameBuffers[12].DepthTextureId);
                w.F("shadowMapWidthInv", 1f / far.Width);
                w.F("shadowMapHeightInv", 1f / far.Height);
                w.F("viewDistance", viewDistance);
                w.F("viewDistanceLod0", viewDistanceLod0);
            }
        }
        if ((mask & 2) != 0)
        {
            w.I("fogSphereQuantity", u.FogSphereQuantity);
            w.Arr("fogSpheres", 1, u.FogSpheres, u.FogSphereQuantity * 8);
            w.I("pointLightQuantity", u.PointLightsCount);
            w.Arr("pointLights", 3, u.PointLights3, u.PointLightsCount);
            w.Arr("pointLightColors", 3, u.PointLightColors3, u.PointLightsCount);
            w.F("flatFogDensity", u.FlagFogDensity);
            w.F("flatFogStart", u.FlatFogStartYPos - u.PlayerPos.Y);
            w.F("glitchStrengthFL", u.GlitchStrength);
            w.F("viewDistance", viewDistance);
            w.F("viewDistanceLod0", viewDistanceLod0);
            w.F("nightVisionStrength", u.NightVisionStrength);
        }
        if ((mask & 4) != 0)
        {
            w.F("shadowRangeNear", u.ShadowRangeNear);
            w.F("shadowRangeFar", u.ShadowRangeFar);
            w.Arr("toShadowMapSpaceMatrixNear", 16, u.ToShadowMapSpaceMatrixNear, 1);
            w.Arr("toShadowMapSpaceMatrixFar", 16, u.ToShadowMapSpaceMatrixFar, 1);
        }
        if ((mask & 8) != 0)
        {
            w.F("timeCounter", u.TimeCounter);
            w.F("windWaveCounter", u.WindWaveCounter);
            w.F("windWaveCounterHighFreq", u.WindWaveCounterHighFreq);
            w.F("windSpeed", u.WindSpeed);
            w.F("waterWaveCounter", u.WaterWaveCounter);
            w.V3("playerpos", u.PlayerPos);
            w.F("globalWarpIntensity", u.GlobalWorldWarp);
            w.F("glitchWaviness", u.GlitchWaviness);
            w.F("windWaveIntensity", u.WindWaveIntensity);
            w.F("waterWaveIntensity", u.WaterWaveIntensity);
            w.I("perceptionEffectId", u.PerceptionEffectId);
            w.F("perceptionEffectIntensity", u.PerceptionEffectIntensity);
        }
        if ((mask & 16) != 0)
        {
            w.F("fogWaveCounter", u.FogWaveCounter);
            w.Tex("sky", u.SkyTextureId);
            w.Tex("glow", u.GlowTextureId);
            w.F("sunsetMod", u.SunsetMod);
            w.I("ditherSeed", u.DitherSeed);
            w.I("horizontalResolution", u.FrameWidth);
            w.F("playerToSealevelOffset", u.PlayerToSealevelOffset);
        }
        if ((mask & 32) != 0)
        {
            w.Arr("colorMapRects", 4, u.ColorMapRects4, 40);
            w.F("seasonRel", u.SeasonRel);
            w.F("seaLevel", u.SeaLevel);
            w.F("atlasHeight", u.BlockAtlasHeight);
            w.F("seasonTemperature", u.SeasonTemperature);
        }
        if ((mask & 64) != 0)
        {
            w.Tex("liquidDepth", platform.FrameBuffers[5].DepthTextureId);
            w.F("cameraUnderwater", u.CameraUnderwater);
            w.V4("waterMurkColor", u.WaterMurkColor);
            var primary = platform.FrameBuffers[0];
            w.V2("frameSize", primary.Width, primary.Height);
        }
        if ((mask & 128) != 0) w.V3("lightPosition", GuiLight);
    }

    private static void Upload(State s, bool force)
    {
        if (!Assert(s.Cur.Length == s.Prev.Length) || !Assert(s.Slots.Length <= s.Cur.Length)) return;
        var cur = MemoryMarshal.Cast<float, int>(s.Cur.AsSpan());
        var prev = MemoryMarshal.Cast<float, int>(s.Prev.AsSpan());
        for (var i = 0; i < Math.Min(s.Slots.Length, MaxSlots); i++)
        {
            ref var slot = ref s.Slots[i];
            var floats = slot.Count * Math.Max(slot.Kind, 1);
            if (floats == 0 || !Assert(slot.Offset + floats <= cur.Length)) continue;
            if (!force && !slot.Dirty && cur.Slice(slot.Offset, floats).SequenceEqual(prev.Slice(slot.Offset, floats)))
            {
                if (Stats) Skips++;
                continue;
            }
            if (Stats) Uploads++;
            ref var v = ref s.Cur[slot.Offset];
            switch (slot.Kind)
            {
                case 0: GL.Uniform1(slot.Loc, slot.Count, ref Unsafe.As<float, int>(ref v)); break;
                case 1: GL.Uniform1(slot.Loc, slot.Count, ref v); break;
                case 2: GL.Uniform2(slot.Loc, slot.Count, ref v); break;
                case 3: GL.Uniform3(slot.Loc, slot.Count, ref v); break;
                case 4: GL.Uniform4(slot.Loc, slot.Count, ref v); break;
                default: GL.UniformMatrix4(slot.Loc, slot.Count, false, ref v); break;
            }
        }
        (s.Prev, s.Cur) = (s.Cur, s.Prev);
    }
}
