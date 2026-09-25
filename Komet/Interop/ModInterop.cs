using System.Collections.Generic;
using System.Reflection;

namespace Komet.Interop;

internal static class ModInterop
{
    private const int MaxAssemblies = 256;
    private const int MaxTypesPerAssembly = 4096;
    private const int MaxNegotiatedFeatures = 32;

    public const string OptimumProviderId = "optimum";
    public const string OptimumAdaptiveRadius = "optimum.adaptive-radius";
    public static bool TryGetFeatureState(string? providerId, string? featureId, out bool active)
    {
        active = false;
        if (!NotNull(providerId) || !NotNull(featureId) || !Assert(providerId.Length > 0) || !Assert(featureId.Length > 0)) return false;

        Type? provider = FindProvider(providerId);
        MethodInfo? method = provider == null ? null : FindMethod(provider, "TryGetFeatureState", typeof(bool), typeof(string), typeof(bool).MakeByRefType());
        if (method == null) return false;

        try
        {
            object?[] args = { featureId, false };
            if (method.Invoke(null, args) is not true) return false;
            active = args[1] is true;
            return true;
        }
        catch (TargetInvocationException) { return false; }
        catch (ArgumentException) { return false; }
        catch (MemberAccessException) { return false; }
    }

    public static bool TryNegotiate(string? providerId, string? requesterId, string[]? activeFeatures, out string[] yieldedFeatures)
    {
        yieldedFeatures = [];
        if (!NotNull(providerId) || !NotNull(requesterId) || !NotNull(activeFeatures) ||
            !Assert(providerId.Length > 0) || !Assert(requesterId.Length > 0) ||
            !Assert(activeFeatures.Length <= MaxNegotiatedFeatures)) return false;
        if (activeFeatures.Length > MaxNegotiatedFeatures) return false;

        Type? provider = FindProvider(providerId);
        MethodInfo? method = provider == null ? null : FindMethod(provider, "Negotiate", typeof(string[]), typeof(string), typeof(string[]));
        if (method == null) return false;

        try
        {
            object? result = method.Invoke(null, new object?[] { requesterId, activeFeatures });
            if (result is not string[] features) return false;
            if (features.Length > MaxNegotiatedFeatures)
            {
                Release(providerId, requesterId, features);
                return false;
            }
            yieldedFeatures = features;
            return true;
        }
        catch (TargetInvocationException) { return false; }
        catch (ArgumentException) { return false; }
        catch (MemberAccessException) { return false; }
    }

    public static void Release(string? providerId, string? requesterId, string[]? yieldedFeatures)
    {
        if (!NotNull(providerId) || !NotNull(requesterId) || !Assert(providerId.Length > 0) || !Assert(requesterId.Length > 0)) return;
        if (yieldedFeatures == null || yieldedFeatures.Length == 0) return;
        Type? provider = FindProvider(providerId);
        MethodInfo? method = provider == null ? null : FindMethod(provider, "Release", typeof(void), typeof(string), typeof(string));
        if (method == null) return;

        for (int i = 0; i < Math.Min(yieldedFeatures.Length, MaxNegotiatedFeatures); i++)
        {
            try { _ = method.Invoke(null, new object?[] { yieldedFeatures[i], requesterId }); }
            catch (TargetInvocationException) { return; }
            catch (ArgumentException) { return; }
            catch (MemberAccessException) { return; }
        }
    }

    private static MethodInfo? FindMethod(Type? type, string? name, Type? returnType, params Type[]? parameterTypes)
    {
        if (!NotNull(type) || !NotNull(name) || !NotNull(returnType) || !NotNull(parameterTypes) ||
            !Assert(name.Length > 0) || !Assert(parameterTypes.Length <= 4)) return null;
        try
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null);
            return method?.ReturnType == returnType ? method : null;
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
    }

    private static Type? FindProvider(string? providerId)
    {
        if (!NotNull(providerId) || !Assert(providerId.Length > 0) || !Assert(MaxAssemblies > 0 && MaxTypesPerAssembly > 0)) return null;
        foreach (Assembly assembly in ((IEnumerable<Assembly>)AppDomain.CurrentDomain.GetAssemblies()).Bounded(MaxAssemblies))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException) { continue; }
            catch (FileLoadException) { continue; }
            catch (FileNotFoundException) { continue; }
            catch (BadImageFormatException) { continue; }

            foreach (Type type in ((IEnumerable<Type>)types).Bounded(MaxTypesPerAssembly))
            {
                if (ConstString(type, "ProtocolId") == VsModInterop.ProtocolId &&
                    ConstString(type, "ProviderId") == providerId) return type;
            }
        }

        return null;
    }

    internal static string? ConstString(Type? type, string? name)
    {
        if (!NotNull(type) || !NotNull(name) || !Assert(name.Length > 0) || !Assert(name.Length <= 64)) return null;
        try
        {
            return type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
