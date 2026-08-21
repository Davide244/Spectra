using System;
using System.Runtime.InteropServices;

namespace SpectraEngine.Physics.Box3D.Native;

/// <summary>Pre-sized capacities for a world's internal arrays.</summary>
/// <remarks>Nested by value inside <see cref="B3WorldDef"/>, not by pointer.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3Capacity
{
    public int StaticShapeCount;
    public int DynamicShapeCount;
    public int StaticBodyCount;
    public int DynamicBodyCount;
    public int ContactCount;
}

/// <summary>
/// The parameters a physics world is created from.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEVER construct one of these in C#. Always take
/// <c>b3DefaultWorldDef()</c> and mutate it.</b> The trailing
/// <see cref="InternalValue"/> must carry a secret cookie that the library
/// checks — and that check <em>compiles away in Release</em>. So a
/// hand-assembled def is not rejected in a shipping build; it is accepted, and
/// a world is built out of unvalidated bytes. There is no diagnostic for that
/// and no crash at the point of the mistake.
/// </para>
/// <para>
/// The default def is also already <em>serial</em>: it leaves
/// <see cref="WorkerCount"/> at zero with both task callbacks null, which is
/// the branch that forces single-threaded execution. Setting
/// <see cref="WorkerCount"/> above one makes the library spawn its own OS
/// threads.
/// </para>
/// <para>
/// <b>The <c>bool</c> fields are <see cref="byte"/> here, and that is not a
/// style choice.</b> C <c>bool</c> is one byte; a C# <c>bool</c> would be
/// widened to four by the marshaller and shift every field after it. This
/// assembly disables runtime marshalling precisely so that mistake cannot
/// compile.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3WorldDef
{
    public B3Vec3 Gravity;
    public float RestitutionThreshold;
    public float HitEventThreshold;
    public float ContactHertz;
    public float ContactDampingRatio;
    public float ContactSpeed;
    public float MaximumLinearSpeed;

    /// <summary>Optional friction-mixing callback. Null unless deliberately set.</summary>
    public nint FrictionCallback;

    /// <summary>Optional restitution-mixing callback. Null unless deliberately set.</summary>
    public nint RestitutionCallback;

    /// <summary>C <c>bool</c>: one byte. Non-zero enables sleeping.</summary>
    public byte EnableSleep;

    /// <summary>C <c>bool</c>: one byte. Non-zero enables continuous collision.</summary>
    public byte EnableContinuous;

    public uint WorkerCount;

    /// <summary>
    /// Task-system callbacks. <b>Both must stay null for a serial world</b> —
    /// the library's three-way branch takes the external-task-system path when
    /// <see cref="WorkerCount"/> is non-zero <em>and both of these are
    /// non-null</em>, so a stray pointer here silently reroutes the solver.
    /// </summary>
    public nint EnqueueTask;

    /// <inheritdoc cref="EnqueueTask"/>
    public nint FinishTask;

    public nint UserTaskContext;
    public nint UserData;
    public nint CreateDebugShape;
    public nint DestroyDebugShape;
    public nint UserDebugShapeContext;
    public B3Capacity Capacity;

    /// <summary>
    /// The library's own construction cookie. <b>Never assign this.</b> It
    /// arrives set from <c>b3DefaultWorldDef()</c> and is the only thing
    /// distinguishing a real def from arbitrary memory — in a Debug build.
    /// </summary>
    public int InternalValue;
}
