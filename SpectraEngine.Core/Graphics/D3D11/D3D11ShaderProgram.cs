using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// A linked VS+PS pair plus the metadata needed to drive them: per-cbuffer GPU
/// buffers with CPU shadows for dirty-tracked uniform updates, and a name → slot
/// map for texture/sampler bindings, both populated via D3D11 shader reflection.
/// </summary>
internal sealed unsafe class D3D11ShaderProgram : ShaderProgram
{
    private readonly D3DCompiler _compiler;
    private readonly ComPtr<ID3D11Device> _device;
    private readonly ComPtr<ID3D11DeviceContext> _context;

    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private byte[] _vsBytecode;

    // One slot per HLSL cbuffer the shader uses; index matches D3D register(bN).
    private CBufferSlot[] _cbuffers = Array.Empty<CBufferSlot>();

    // Variable name → (cbuffer index, offset bytes, size bytes).
    private Dictionary<string, UniformLocation> _uniforms = new(StringComparer.Ordinal);

    // Texture/sampler register slot lookup. SpectraShade emits the SRV and the
    // SamplerState at the same register index, so one number serves both.
    private Dictionary<string, uint> _textureSlots = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>The VS bytecode used for input-layout creation by D3D11Mesh.</summary>
    public ReadOnlyMemory<byte> VertexBytecode => _vsBytecode;

    private D3D11ShaderProgram(
        D3DCompiler compiler,
        ComPtr<ID3D11Device> device,
        ComPtr<ID3D11DeviceContext> context,
        ComPtr<ID3D11VertexShader> vs,
        ComPtr<ID3D11PixelShader> ps,
        byte[] vsBytecode,
        byte[] psBytecode)
    {
        _compiler = compiler;
        _device = device;
        _context = context;
        _vs = vs;
        _ps = ps;
        _vsBytecode = vsBytecode;
        BuildReflection(psBytecode);
    }

    internal static D3D11ShaderProgram Create(
        D3DCompiler compiler,
        ComPtr<ID3D11Device> device,
        ComPtr<ID3D11DeviceContext> context,
        string vertexSource,
        string fragmentSource)
    {
        byte[] vsBlob = CompileStage(compiler, vertexSource, "vs_5_0", "Vertex");
        byte[] psBlob = CompileStage(compiler, fragmentSource, "ps_5_0", "Fragment");

        ID3D11VertexShader* vsPtr = null;
        ID3D11PixelShader* psPtr = null;
        var dev = (ID3D11Device*)device.Handle;
        fixed (byte* pVs = vsBlob)
        fixed (byte* pPs = psBlob)
        {
            SilkMarshal.ThrowHResult(dev->CreateVertexShader(pVs, (nuint)vsBlob.Length, null, &vsPtr));
            SilkMarshal.ThrowHResult(dev->CreatePixelShader(pPs, (nuint)psBlob.Length, null, &psPtr));
        }

        return new D3D11ShaderProgram(compiler, device, context,
            ComOwnership.Own(vsPtr),
            ComOwnership.Own(psPtr),
            vsBlob, psBlob);
    }

    public override bool TryReload(PipelineBlob blob, [NotNullWhen(false)] out string? error)
    {
        if (blob.Backend != GraphicsBackend.D3D11) { error = $"Expected D3D11 blob, got {blob.Backend}"; return false; }
        if (blob.Format != ShaderDataFormat.SourceText) { error = $"D3D11 requires SourceText, got {blob.Format}"; return false; }

        string vsSrc = Encoding.UTF8.GetString(blob.VertexData
            ?? throw new InvalidOperationException("Reload blob has no vertex stage"));
        string psSrc = Encoding.UTF8.GetString(blob.FragmentData
            ?? throw new InvalidOperationException("Reload blob has no fragment stage"));

        byte[] vsBlob, psBlob;
        try
        {
            vsBlob = CompileStage(_compiler, vsSrc, "vs_5_0", "Vertex");
            psBlob = CompileStage(_compiler, psSrc, "ps_5_0", "Fragment");
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }

        ID3D11VertexShader* newVsPtr = null;
        ID3D11PixelShader* newPsPtr = null;
        try
        {
            var dev = (ID3D11Device*)_device.Handle;
            fixed (byte* pVs = vsBlob)
            fixed (byte* pPs = psBlob)
            {
                SilkMarshal.ThrowHResult(dev->CreateVertexShader(pVs, (nuint)vsBlob.Length, null, &newVsPtr));
                SilkMarshal.ThrowHResult(dev->CreatePixelShader(pPs, (nuint)psBlob.Length, null, &newPsPtr));
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (newVsPtr is not null) newVsPtr->Release();
            if (newPsPtr is not null) newPsPtr->Release();
            return false;
        }

        // Swap atomically; release old GPU resources after the new ones are in.
        var oldVs = _vs;
        var oldPs = _ps;
        DisposeCBuffers();
        _vs = ComOwnership.Own(newVsPtr);
        _ps = ComOwnership.Own(newPsPtr);
        _vsBytecode = vsBlob;
        BuildReflection(psBlob);
        oldVs.Dispose();
        oldPs.Dispose();

        error = null;
        return true;
    }

    private static byte[] CompileStage(D3DCompiler compiler, string source, string profile, string stageLabel)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        ComPtr<ID3D10Blob> code = default;
        ComPtr<ID3D10Blob> errors = default;

        fixed (byte* pSrc = sourceBytes)
        fixed (byte* pProfile = Encoding.ASCII.GetBytes(profile + "\0"))
        fixed (byte* pEntry = "main\0"u8)
        fixed (byte* pName = Encoding.ASCII.GetBytes(stageLabel + "\0"))
        {
            int hr = compiler.Compile(
                pSrc,
                (nuint)sourceBytes.Length,
                pName,
                null,
                ref Unsafe.NullRef<ID3DInclude>(),
                pEntry,
                pProfile,
                0u,
                0u,
                ref code,
                ref errors);

            if (hr < 0)
            {
                string msg = ReadBlobString(errors);
                errors.Dispose();
                code.Dispose();
                throw new InvalidOperationException(
                    $"{stageLabel} HLSL compile failed ({hr:X}): {msg}");
            }
        }

        byte[] bytecode = ReadBlobBytes(code);
        code.Dispose();
        errors.Dispose();
        return bytecode;
    }

    private static byte[] ReadBlobBytes(ComPtr<ID3D10Blob> blob)
    {
        if (blob.Handle is null) return Array.Empty<byte>();
        nuint len = blob.GetBufferSize();
        void* ptr = blob.GetBufferPointer();
        var bytes = new byte[(int)len];
        new ReadOnlySpan<byte>(ptr, (int)len).CopyTo(bytes);
        return bytes;
    }

    private static string ReadBlobString(ComPtr<ID3D10Blob> blob)
    {
        if (blob.Handle is null) return "(no error blob)";
        nuint len = blob.GetBufferSize();
        void* ptr = blob.GetBufferPointer();
        return Encoding.UTF8.GetString((byte*)ptr, (int)len).TrimEnd('\0', '\n', '\r');
    }

    // ─── Reflection ──────────────────────────────────────────

    private void BuildReflection(byte[] psBytecode)
    {
        _uniforms = new Dictionary<string, UniformLocation>(StringComparer.Ordinal);
        _textureSlots = new Dictionary<string, uint>(StringComparer.Ordinal);
        var cbuffers = new List<CBufferSlot>();

        // Both VS and PS contribute uniforms; reflect both and merge.
        ReflectStage(_vsBytecode, cbuffers, isPs: false);
        ReflectStage(psBytecode, cbuffers, isPs: true);

        _cbuffers = cbuffers.ToArray();
    }

    private void ReflectStage(byte[] bytecode, List<CBufferSlot> cbuffers, bool isPs)
    {
        ID3D11ShaderReflection* reflPtr = null;
        Guid iid = ID3D11ShaderReflection.Guid;
        fixed (byte* pBytecode = bytecode)
        {
            SilkMarshal.ThrowHResult(_compiler.Reflect(
                pBytecode,
                (nuint)bytecode.Length,
                &iid,
                (void**)&reflPtr));
        }

        ShaderDesc shaderDesc = default;
        SilkMarshal.ThrowHResult(reflPtr->GetDesc(&shaderDesc));

        // Each constant buffer used by this stage. ConstantBuffers is indexed
        // by declaration order, not register slot — we look up the bind slot
        // separately via GetResourceBindingDescByName.
        for (uint i = 0; i < shaderDesc.ConstantBuffers; i++)
        {
            ID3D11ShaderReflectionConstantBuffer* cb = reflPtr->GetConstantBufferByIndex(i);
            ShaderBufferDesc bufDesc = default;
            SilkMarshal.ThrowHResult(cb->GetDesc(&bufDesc));

            string name = MarshalUtf8(bufDesc.Name);
            ShaderInputBindDesc bindDesc = default;
            SilkMarshal.ThrowHResult(reflPtr->GetResourceBindingDescByName(bufDesc.Name, &bindDesc));
            uint slot = bindDesc.BindPoint;

            // If we already saw this cbuffer in a previous stage, reuse its
            // backing buffer + shadow and just record stage visibility.
            int existing = cbuffers.FindIndex(c => c.Slot == slot && c.Name == name);
            if (existing >= 0)
            {
                cbuffers[existing] = cbuffers[existing] with
                {
                    UsedInVs = cbuffers[existing].UsedInVs || !isPs,
                    UsedInPs = cbuffers[existing].UsedInPs || isPs,
                };
                continue;
            }

            uint sizeBytes = bufDesc.Size;
            uint roundedSize = ((sizeBytes + 15u) / 16u) * 16u;

            var buffer = CreateCBuffer(_device, roundedSize);
            byte[] shadow = new byte[roundedSize];

            var slotInfo = new CBufferSlot(name, slot, buffer, shadow, dirty: true, usedInVs: !isPs, usedInPs: isPs);
            cbuffers.Add(slotInfo);
            int newIdx = cbuffers.Count - 1;

            // Register every member of this cbuffer in the uniform map.
            for (uint v = 0; v < bufDesc.Variables; v++)
            {
                ID3D11ShaderReflectionVariable* variable = cb->GetVariableByIndex(v);
                ShaderVariableDesc varDesc = default;
                SilkMarshal.ThrowHResult(variable->GetDesc(&varDesc));
                string varName = MarshalUtf8(varDesc.Name);
                _uniforms[varName] = new UniformLocation(newIdx, varDesc.StartOffset, varDesc.Size);
            }
        }

        // Texture and sampler bindings (textures: D3D_SIT_TEXTURE; samplers: D3D_SIT_SAMPLER).
        // The HLSL generator emits both at the same register index per source-level
        // sampler, so storing one slot for the texture name is sufficient.
        for (uint i = 0; i < shaderDesc.BoundResources; i++)
        {
            ShaderInputBindDesc bind = default;
            SilkMarshal.ThrowHResult(reflPtr->GetResourceBindingDesc(i, &bind));
            if (bind.Type != D3DShaderInputType.D3DSitTexture) continue;
            string name = MarshalUtf8(bind.Name);
            _textureSlots[name] = bind.BindPoint;
        }

        reflPtr->Release();
    }

    private static ComPtr<ID3D11Buffer> CreateCBuffer(ComPtr<ID3D11Device> device, uint sizeBytes)
    {
        var desc = new BufferDesc
        {
            ByteWidth = sizeBytes,
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
            MiscFlags = 0,
            StructureByteStride = 0,
        };
        ID3D11Buffer* bufPtr = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)device.Handle)->CreateBuffer(&desc, null, &bufPtr));
        return ComOwnership.Own(bufPtr);
    }

    private static string MarshalUtf8(byte* ptr)
    {
        if (ptr is null) return string.Empty;
        int len = 0;
        while (ptr[len] != 0) len++;
        return Encoding.UTF8.GetString(ptr, len);
    }

    // ─── Use / SetUniform / SetTexture ───────────────────────

    public override void Use()
    {
        var ctx = (ID3D11DeviceContext*)_context.Handle;

        // Flush any dirty cbuffer shadows to GPU, then bind shaders and cbuffers.
        for (int i = 0; i < _cbuffers.Length; i++)
        {
            ref var slot = ref _cbuffers[i];
            if (slot.Dirty)
            {
                MappedSubresource mapped = default;
                SilkMarshal.ThrowHResult(ctx->Map(
                    (ID3D11Resource*)slot.Buffer.Handle, 0, Map.WriteDiscard, 0, &mapped));
                fixed (byte* src = slot.Shadow)
                {
                    System.Buffer.MemoryCopy(src, mapped.PData, slot.Shadow.Length, slot.Shadow.Length);
                }
                ctx->Unmap((ID3D11Resource*)slot.Buffer.Handle, 0);
                slot.Dirty = false;
            }
        }

        ctx->VSSetShader((ID3D11VertexShader*)_vs.Handle, null, 0);
        ctx->PSSetShader((ID3D11PixelShader*)_ps.Handle, null, 0);

        for (int i = 0; i < _cbuffers.Length; i++)
        {
            ref var slot = ref _cbuffers[i];
            ID3D11Buffer* buf = (ID3D11Buffer*)slot.Buffer.Handle;
            if (slot.UsedInVs) ctx->VSSetConstantBuffers(slot.Slot, 1, &buf);
            if (slot.UsedInPs) ctx->PSSetConstantBuffers(slot.Slot, 1, &buf);
        }
    }

    public override void SetUniform(string name, Matrix4x4 value) => Write(name, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));
    public override void SetUniform(string name, Vector4 value) => Write(name, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));
    public override void SetUniform(string name, Vector3 value)
    {
        // Pad to 16 bytes since HLSL cbuffer fields don't cross 16-byte
        // boundaries. The reflected size for a float3 field is 12, so we only
        // write the 12 bytes the reflection asked for.
        Span<float> tmp = stackalloc float[4] { value.X, value.Y, value.Z, 0f };
        Write(name, MemoryMarshal.AsBytes(tmp));
    }
    public override void SetUniform(string name, Vector2 value) => Write(name, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));
    public override void SetUniform(string name, float value) => Write(name, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));
    public override void SetUniform(string name, int value) => Write(name, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));

    private void Write(string name, ReadOnlySpan<byte> bytes)
    {
        if (!_uniforms.TryGetValue(name, out var loc)) return;
        ref var slot = ref _cbuffers[loc.CBufferIndex];
        int copyLen = Math.Min((int)loc.Size, bytes.Length);
        bytes.Slice(0, copyLen).CopyTo(slot.Shadow.AsSpan((int)loc.Offset));
        slot.Dirty = true;
    }

    public override void SetTexture(string name, int unit, Texture texture)
    {
        if (texture is not D3D11Texture d3dTex)
            throw new ArgumentException($"Texture must be D3D11Texture, got {texture.GetType().Name}", nameof(texture));
        if (!_textureSlots.TryGetValue(name, out uint slot)) return;

        var ctx = (ID3D11DeviceContext*)_context.Handle;
        ID3D11ShaderResourceView* srv = (ID3D11ShaderResourceView*)d3dTex.Srv.Handle;
        ID3D11SamplerState* samp = (ID3D11SamplerState*)d3dTex.Sampler.Handle;
        ctx->PSSetShaderResources(slot, 1, &srv);
        ctx->PSSetSamplers(slot, 1, &samp);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCBuffers();
        _ps.Dispose();
        _vs.Dispose();
    }

    private void DisposeCBuffers()
    {
        for (int i = 0; i < _cbuffers.Length; i++)
            _cbuffers[i].Buffer.Dispose();
        _cbuffers = Array.Empty<CBufferSlot>();
    }

    private record struct UniformLocation(int CBufferIndex, uint Offset, uint Size);

    private struct CBufferSlot
    {
        public string Name;
        public uint Slot;
        public ComPtr<ID3D11Buffer> Buffer;
        public byte[] Shadow;
        public bool Dirty;
        public bool UsedInVs;
        public bool UsedInPs;

        public CBufferSlot(string name, uint slot, ComPtr<ID3D11Buffer> buffer, byte[] shadow, bool dirty, bool usedInVs, bool usedInPs)
        {
            Name = name;
            Slot = slot;
            Buffer = buffer;
            Shadow = shadow;
            Dirty = dirty;
            UsedInVs = usedInVs;
            UsedInPs = usedInPs;
        }
    }
}
