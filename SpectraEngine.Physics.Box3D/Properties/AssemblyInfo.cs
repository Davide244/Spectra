using System.Runtime.CompilerServices;


// Turns off the runtime marshalling layer for every P/Invoke in this assembly.
//
// This is the load-bearing choice of the whole binding, not a performance knob.
// With it on, a struct that is not blittable is a COMPILE error rather than a
// silent reinterpretation at the boundary — which is precisely the failure class
// this binding exists to defend against, and the one the ABI manifest can only
// catch for structs somebody remembered to list.
//
// The concrete consequence: a C `bool` field cannot be a C# `bool` here. C bool
// is one byte; the runtime marshaller would widen it to four and shift every
// field after it. Under this attribute the compiler refuses instead, so the
// `byte`-and-convert discipline is enforced by the toolchain rather than by
// whoever is reading the header that day.
[assembly: DisableRuntimeMarshalling]

// The raw entry points stay internal: they are an unwrapped transcription with
// no null checks, no lifetime rules and no id validation, and every one of
// those belongs a layer up. The test assembly is granted access anyway, because
// the binding is exactly the layer whose correctness has to be proved against
// the real library rather than through a wrapper that could paper over it.
[assembly: InternalsVisibleTo("SpectraEngine.Physics.Tests")]
