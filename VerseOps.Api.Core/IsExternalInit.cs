// IsExternalInit shim required for C# 9+ `init` accessors (records use init
// for their positional properties). The compiler emits a reference to
// System.Runtime.CompilerServices.IsExternalInit; the type only exists in
// net5.0+ BCL. On netstandard2.0 we have to provide it ourselves.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
