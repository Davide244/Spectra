using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Spectra.Kitchen.Cache;

/// <summary>
/// The vector instruction-set baseline this cook is running under, as one stable
/// token that goes into every cache key.
/// </summary>
/// <remarks>
/// <para><b>This is in the key because of a measurement, not a precaution.</b>
/// <c>docs/spikes/2026-09-cook-dependency-spikes.md</c> encoded the same PNG with
/// the same encoder and the same settings and got two different BC7 payloads: 310
/// of 1,024 blocks differed between an AVX2 baseline and a non-AVX2 one, at a
/// 0.14 dB quality difference and a maximum per-channel decode delta of 4/255. The
/// two outputs are visually equivalent and byte-different, which is the worst
/// shape this could have taken, because nothing in the artifact signals that
/// anything changed. A content-addressed cache keyed on source plus settings alone
/// hands one host the other host's artifact and says "unchanged, skip it".</para>
/// <para><b>The exposure is concrete and already exists in this repo.</b> The
/// editor hosts the cooking library in process and may run JIT under
/// <c>dotnet run</c>, where the runtime adapts to the host CPU; a published
/// <c>scook</c> is NativeAOT, where the baseline is baked at compile time. The
/// spike measured a default AOT binary producing the non-AVX2 result WHILE RUNNING
/// ON AN AVX2 MACHINE, so those two processes disagree about BC7 on one machine
/// with one source file. <see cref="Token"/> separates them.</para>
/// <para><b>The pin is the strong form and the probe is the weak one, and the
/// difference is stated rather than hidden.</b> A cook published with a fixed
/// <c>IlcInstructionSet</c> knows its baseline as a build-time constant and should
/// declare it through <see cref="Pinned"/>, which makes the mismatch impossible.
/// The probe only makes it VISIBLE: it reports what the process says it supports,
/// and under NativeAOT an instruction set can be reported as available at runtime
/// while the code that was compiled ahead of time did not use it. So the probe can
/// still, in principle, call two different baselines the same thing. It cannot
/// call one baseline two things, which is the direction that would thrash rather
/// than corrupt.</para>
/// <para><b>It is unconditional, not scoped to the rules that encode blocks.</b>
/// Only BC7 is known to be affected today and there is no image rule yet, but a
/// cache that under-invalidates ships wrong bytes while one that over-invalidates
/// costs a rebuild, and nothing here can know which future rule is sensitive. The
/// price is that a cache is not shared between a JIT host and an AOT one; that
/// price is exactly what the spike says it should be.</para>
/// </remarks>
public static class InstructionSetBaseline
{
    private static string? _pinned;
    private static string? _probed;

    /// <summary>
    /// The baseline this cook binary was COMPILED for, when it knows one.
    /// </summary>
    /// <remarks>
    /// Set once at startup by a host published with a fixed
    /// <c>IlcInstructionSet</c>, which is the same discipline
    /// <c>native/build-box3d.ps1</c> applies to ABI-affecting options. Null means
    /// "ask the process", which is <see cref="Token"/>'s fallback and the weaker
    /// of the two answers.
    /// </remarks>
    public static string? Pinned
    {
        get => _pinned;
        set => _pinned = value;
    }

    /// <summary>
    /// The token the cache key carries: the pin if one was declared, else a probe
    /// of this process.
    /// </summary>
    /// <remarks>
    /// The probe is memoised because it is a pure function of the process and is
    /// asked once per rule. A race between two threads computing it produces the
    /// same string twice, so no lock is needed.
    /// </remarks>
    public static string Token => _pinned ?? (_probed ??= Probe());

    private static string Probe()
    {
        var token = new StringBuilder(96);

        // JIT and AOT are separated even when the flags below agree, because the
        // spike's decisive measurement was exactly that pair on one machine: the
        // JIT adapts to the host CPU and a default AOT publish does not.
        token.Append(RuntimeFeature.IsDynamicCodeCompiled ? "jit" : "aot");
        token.Append(';');
        token.Append(DescribeArchitecture(RuntimeInformation.ProcessArchitecture));

        // Vector<T>'s width is fixed when the code is compiled rather than when it
        // runs, so it is a property of the baseline in its own right.
        token.Append(";vt");
        token.Append(System.Numerics.Vector<byte>.Count.ToString(CultureInfo.InvariantCulture));
        token.Append(';');

        // Hand-written and APPEND-ONLY. Every flag is written with its value
        // rather than only when set, so adding one at the end changes the token
        // for everybody exactly once; removing one silently merges two baselines
        // into a single cache identity, which is the failure this file exists to
        // prevent.
        Flag(token, "sse42", Sse42.IsSupported, first: true);
        Flag(token, "avx", Avx.IsSupported);
        Flag(token, "avx2", Avx2.IsSupported);
        Flag(token, "fma", Fma.IsSupported);
        Flag(token, "avx512f", Avx512F.IsSupported);
        Flag(token, "advsimd", AdvSimd.IsSupported);

        return token.ToString();
    }

    private static void Flag(StringBuilder into, string name, bool supported, bool first = false)
    {
        if (!first) into.Append(',');
        into.Append(name);
        into.Append('=');
        into.Append(supported ? '1' : '0');
    }

    // Hand-written rather than ToString(): an enum's name comes back through
    // reflection over its metadata, which is what trimming removes.
    private static string DescribeArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x64",
        Architecture.Arm => "arm",
        Architecture.Arm64 => "arm64",
        Architecture.Wasm => "wasm",
        Architecture.S390x => "s390x",
        Architecture.LoongArch64 => "loongarch64",
        Architecture.Armv6 => "armv6",
        Architecture.Ppc64le => "ppc64le",
        Architecture.RiscV64 => "riscv64",

        // Not throwing: an architecture this build has no name for still has to
        // key SOMETHING, and a number is a stable name for it.
        _ => "arch" + ((int)architecture).ToString(CultureInfo.InvariantCulture),
    };
}
