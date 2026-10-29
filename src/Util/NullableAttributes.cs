// Polyfill for the C# nullable-reference-type metadata attributes.
//
// With <Nullable>enable</Nullable>, the compiler must emit
// System.Runtime.CompilerServices.NullableAttribute / NullableContextAttribute /
// NullablePublicOnlyAttribute. Normally it synthesizes them itself. But one of the
// BepInEx/IL2CPP interop assemblies referenced by this project publicly exports those
// types without the constructors the compiler expects, so the compiler binds to the
// external ones and fails the whole build with CS0656 ("Missing compiler required
// member ...NullableAttribute..ctor").
//
// Declaring our own internal copies here makes the compiler use these — defined in the
// assembly under compilation — for its synthesized attributes, sidestepping the broken
// external definitions. This is the same polyfill pattern used to enable nullable on
// down-level targets.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Event | AttributeTargets.Field |
        AttributeTargets.GenericParameter | AttributeTargets.Parameter |
        AttributeTargets.Property | AttributeTargets.ReturnValue,
        AllowMultiple = false, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte flag) { NullableFlags = new[] { flag }; }
        public NullableAttribute(byte[] flags) { NullableFlags = flags; }
    }

    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Interface |
        AttributeTargets.Method | AttributeTargets.Struct,
        AllowMultiple = false, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte flag) { Flag = flag; }
    }

    [AttributeUsage(AttributeTargets.Module, AllowMultiple = false, Inherited = false)]
    internal sealed class NullablePublicOnlyAttribute : Attribute
    {
        public readonly bool IncludesInternals;
        public NullablePublicOnlyAttribute(bool includesInternals) { IncludesInternals = includesInternals; }
    }
}
