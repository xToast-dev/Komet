namespace JetBrains.Annotations;

// ReSharper reads this by name: the delegate a parameter marks runs only while the call lasts, never after it returned
[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class InstantHandleAttribute : Attribute;
