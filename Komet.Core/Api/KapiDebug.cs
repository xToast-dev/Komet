using System.Diagnostics;
using Komet.Core;

namespace Komet.Api;

/// <summary>
///     Timings of the mod's code in Komet's <c>.komet debug</c> protocol, through <see cref="KapiMod.Debug" />.
/// </summary>
/// <example>
///     <code language="csharp">
/// using (komet.Debug.Measure("grade"))
/// {
///     RenderGrade();
/// }
/// </code>
/// </example>
public sealed class KapiDebug
{
    /// <summary>How many characters a timing's name may have, the mod id included.</summary>
    public const int MaxName = 128;

    private readonly KapiMod _mod;

    internal KapiDebug(KapiMod mod) => _mod = mod;

    /// <summary>A capture runs. Work done only for the protocol can test this first.</summary>
    public static bool Capturing => DebugTimings.Capturing;

    /// <summary>
    ///     Times a stretch of code, on any thread, booked as <c>modid:what</c> when the result is disposed. Outside a
    ///     capture it costs a field read and allocates nothing.
    /// </summary>
    /// <param name="what">What is timed.</param>
    /// <returns>The timing; dispose it at the stretch's end (<c>using</c>).</returns>
    public KapiTiming Measure(string what) =>
        DebugTimings.Capturing && !string.IsNullOrEmpty(what) && _mod.ModId.Length + 1 + what.Length <= MaxName
            ? DebugTimings.Measure(_mod.ModId + ":" + what)
            : default;
}

/// <summary>A running timing from <see cref="KapiDebug.Measure" />.</summary>
public readonly struct KapiTiming : IDisposable, IEquatable<KapiTiming>
{
    private readonly string? _name;
    private readonly long _start;

    internal KapiTiming(string name, long start) => (_name, _start) = (name, start);

    /// <summary>Books the time since <see cref="KapiDebug.Measure" />.</summary>
    public void Dispose()
    {
        if (_name is null) return;

        DebugTimings.Book(_name, Stopwatch.GetTimestamp() - _start);
    }

    /// <inheritdoc />
    public bool Equals(KapiTiming other) => _name == other._name && _start == other._start;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KapiTiming other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_name, _start);

    /// <summary>Whether two timings are the same.</summary>
    /// <param name="left">One timing.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they are the same timing.</returns>
    public static bool operator ==(KapiTiming left, KapiTiming right) => left.Equals(right);

    /// <summary>Whether two timings differ.</summary>
    /// <param name="left">One timing.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they differ.</returns>
    public static bool operator !=(KapiTiming left, KapiTiming right) => !left.Equals(right);
}
