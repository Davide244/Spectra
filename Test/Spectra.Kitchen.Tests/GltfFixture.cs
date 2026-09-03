using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// Hand-written glTF 2.0 documents, one triangle each, with a knob per thing the
/// reader is meant to refuse.
/// </summary>
/// <remarks>
/// <para><b>Written from the glTF specification rather than through
/// <see cref="Models.GltfReader"/> or any exporter</b>, for the reason
/// <c>TempProject.Wav</c> gives one format over: a reader checked against a
/// fixture built by its own code proves the two agree rather than that either is
/// right, and every failure in this area is a misread buffer rather than an
/// exception.</para>
/// <para><b>The triangle is asymmetric on every axis and its UVs are not its
/// positions.</b> A fixture that is the same shape flipped, mirrored or
/// transposed makes four different bugs look identical - the same argument the
/// texture orientation probe already records - and equal position and UV values
/// would hide an accessor read at the wrong offset entirely.</para>
/// </remarks>
internal static class GltfFixture
{
    /// <summary>The material name every fixture declares unless told otherwise.</summary>
    public const string MaterialName = "FixtureSurface";

    // (x, y, z) per corner. Distinct on all three axes and none of them zero
    // twice, so a component swap moves a number rather than nothing.
    private static readonly float[] Positions = [0f, 0f, 0f, 2f, 0f, 0.5f, 0f, 3f, 1.5f];

    // Not the face normal, deliberately: the reader must carry what the file
    // says rather than recompute it, and a fixture whose normals happen to equal
    // the cross product cannot tell the two apart.
    private static readonly float[] Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f];

    // v values away from 0 and 1 on two corners, because the engine flips v and
    // 1 - 0 is 1, which is the other corner's value: a fixture of only zeros and
    // ones cannot tell a flip from a swap.
    private static readonly float[] Uvs = [0.25f, 0.75f, 1f, 0.75f, 0.25f, 0.125f];

    private static readonly ushort[] Indices = [0, 1, 2];

    /// <summary>The tightly packed buffer every fixture below indexes into.</summary>
    public static byte[] Buffer()
    {
        var bytes = new byte[BufferLength];
        int at = 0;

        foreach (float value in Positions)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
            at += 4;
        }

        foreach (float value in Normals)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
            at += 4;
        }

        foreach (float value in Uvs)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
            at += 4;
        }

        foreach (ushort value in Indices)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
            at += 2;
        }

        return bytes;
    }

    /// <summary>Positions in the order the buffer holds them.</summary>
    public static ReadOnlySpan<float> ExpectedPositions => Positions;

    /// <summary>UVs as written in the file, i.e. BEFORE the engine's v flip.</summary>
    public static ReadOnlySpan<float> AuthoredUvs => Uvs;

    private const int PositionOffset = 0;
    private const int NormalOffset = 36;
    private const int UvOffset = 72;
    private const int IndexOffset = 96;
    private const int BufferLength = 102;

    /// <summary>
    /// One triangle, everything default, with the buffer inline as a data uri.
    /// </summary>
    public static string Json(
        string materialName = MaterialName,
        int mode = 4,
        bool sparsePositions = false,
        bool omitNormals = false,
        bool omitIndices = false,
        string? requiredExtension = null,
        string? assetVersion = "2.0",
        string? bufferUri = null,
        float[]? nodeTranslation = null,
        float[]? nodeMatrix = null,
        int indexComponentType = 5123,
        string? extraNodeAttribute = null)
    {
        string uri = bufferUri ?? DataUri(Buffer());

        var attributes = new List<string> { "\"POSITION\": 0" };
        if (!omitNormals) attributes.Add("\"NORMAL\": 1");
        attributes.Add("\"TEXCOORD_0\": 2");
        if (extraNodeAttribute is not null) attributes.Add($"\"{extraNodeAttribute}\": 2");

        string primitive =
            $"{{ \"attributes\": {{ {string.Join(", ", attributes)} }}, " +
            (omitIndices ? string.Empty : "\"indices\": 3, ") +
            $"\"material\": 0, \"mode\": {mode} }}";

        string placement = nodeMatrix is not null
            ? $", \"matrix\": [{Numbers(nodeMatrix)}]"
            : nodeTranslation is not null
                ? $", \"translation\": [{Numbers(nodeTranslation)}]"
                : string.Empty;

        string sparse = sparsePositions
            ? ", \"sparse\": { \"count\": 1, \"indices\": { \"bufferView\": 3, \"componentType\": 5123 }, " +
              "\"values\": { \"bufferView\": 0 } }"
            : string.Empty;

        var json = new StringBuilder();
        json.Append('{');
        if (assetVersion is not null)
            json.Append($"\"asset\": {{ \"version\": \"{assetVersion}\" }},");

        if (requiredExtension is not null)
            json.Append($"\"extensionsRequired\": [\"{requiredExtension}\"],");

        json.Append("\"scene\": 0,");
        json.Append("\"scenes\": [ { \"nodes\": [0] } ],");
        json.Append($"\"nodes\": [ {{ \"name\": \"FixtureNode\", \"mesh\": 0{placement} }} ],");
        json.Append($"\"meshes\": [ {{ \"name\": \"FixtureMesh\", \"primitives\": [ {primitive} ] }} ],");
        json.Append(
            $"\"materials\": [ {{ \"name\": \"{materialName}\", \"pbrMetallicRoughness\": " +
            "{ \"baseColorTexture\": { \"index\": 0 } } } ],");
        json.Append("\"textures\": [ { \"source\": 0 } ],");
        json.Append("\"images\": [ { \"uri\": \"../Textures/fixture.png\" } ],");
        json.Append("\"accessors\": [");
        json.Append(
            $"{{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"{sparse}}},");
        json.Append("{\"bufferView\":1,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},");
        json.Append("{\"bufferView\":2,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\"},");
        json.Append(
            $"{{\"bufferView\":3,\"componentType\":{indexComponentType},\"count\":3,\"type\":\"SCALAR\"}}");
        json.Append("],");
        json.Append("\"bufferViews\": [");
        json.Append($"{{\"buffer\":0,\"byteOffset\":{PositionOffset},\"byteLength\":36}},");
        json.Append($"{{\"buffer\":0,\"byteOffset\":{NormalOffset},\"byteLength\":36}},");
        json.Append($"{{\"buffer\":0,\"byteOffset\":{UvOffset},\"byteLength\":24}},");
        json.Append($"{{\"buffer\":0,\"byteOffset\":{IndexOffset},\"byteLength\":6}}");
        json.Append("],");
        json.Append(
            uri.Length == 0
                ? $"\"buffers\": [ {{ \"byteLength\": {BufferLength} }} ]"
                : $"\"buffers\": [ {{ \"byteLength\": {BufferLength}, \"uri\": \"{uri}\" }} ]");
        json.Append('}');
        return json.ToString();
    }

    /// <summary>The same document with its buffer left to the GLB binary chunk.</summary>
    public static string GlbJson() => Json(bufferUri: string.Empty);

    /// <summary>Wraps a document and its buffer in the GLB container.</summary>
    /// <remarks>
    /// Chunks are 4-byte aligned and the padding is NOT counted in the chunk's
    /// own length, which is the one part of the container a hand-written writer
    /// usually gets wrong: without it the next chunk's header is read out of the
    /// padding, as a plausible length and a garbage type.
    /// </remarks>
    public static byte[] Glb(string json, byte[] binary, uint version = 2, int declaredLengthDelta = 0)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPadded = Align4(jsonBytes.Length);
        int binaryPadded = Align4(binary.Length);

        int total = 12 + 8 + jsonPadded + (binary.Length > 0 ? 8 + binaryPadded : 0);
        var file = new byte[total];

        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)(total + declaredLengthDelta));

        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 0x4E4F534A);
        jsonBytes.CopyTo(file, 20);

        // JSON pads with SPACES and BIN with zeros, per the specification. A
        // reader that trimmed on its own would accept either; the fixture writes
        // what a conforming exporter writes.
        for (int i = 20 + jsonBytes.Length; i < 20 + jsonPadded; i++) file[i] = 0x20;

        if (binary.Length > 0)
        {
            int at = 20 + jsonPadded;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), (uint)binaryPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 4), 0x004E4942);
            binary.CopyTo(file, at + 8);
        }

        return file;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static string DataUri(byte[] bytes) =>
        "data:application/octet-stream;base64," + Convert.ToBase64String(bytes);

    // Invariant, because a fixture whose numbers parse on one machine and not on
    // another is the worst kind of test failure to receive.
    private static string Numbers(float[] values) =>
        string.Join(", ", Array.ConvertAll(values, v => v.ToString("R", CultureInfo.InvariantCulture)));
}
