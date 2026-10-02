// netstandard2.1 shim: C# 9 `record` types require the IsExternalInit
// machinery, which is only built into .NET 5+. Polyfill it for Unity
// compatibility (knowledge.md rule 4).
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
