// netstandard2.1 predates C# 9 and therefore does not ship the
// System.Runtime.CompilerServices.IsExternalInit marker type that the compiler
// requires for `init` accessors and `record` types. Supplying it here is what lets
// Core use immutable records while still targeting netstandard2.1 for Unity.
//
// This is a compiler-shim only: it has no runtime behaviour.

namespace System.Runtime.CompilerServices;

using System.ComponentModel;

/// <summary>Compiler marker for init-only setters. Never called at runtime.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
