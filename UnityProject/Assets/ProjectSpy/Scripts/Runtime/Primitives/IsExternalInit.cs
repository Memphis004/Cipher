// Unity 2022.3 compiles against a C# 9 profile whose BCL predates `init` accessors, so
// the compiler has no `System.Runtime.CompilerServices.IsExternalInit` to bind them to and
// every `init` property in this assembly fails with CS0518.
//
// ProjectSpy.Core ships its own copy of this type for the same reason. It is duplicated
// here rather than shared because Core's copy lives inside an assembly Presentation must
// not take a code dependency on for a single empty class.
//
// This file is compiler support, not game code: the type is never instantiated and never
// referenced. Deleting it breaks the build in a way that looks unrelated to blockout.

namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    /// <summary>
    /// Marker the compiler requires on an accessor marked <c>init</c>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}