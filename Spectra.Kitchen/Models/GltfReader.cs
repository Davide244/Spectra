using SpectraEngine.Core.Assets;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace Spectra.Kitchen.Models;

/// <summary>
/// Where an external glTF buffer's bytes come from.
/// </summary>
/// <param name="contentPath">
/// The buffer's URI, already joined against the model's own folder and
/// normalised to a content-relative path.
/// </param>
/// <returns>The bytes, or null when nothing is there.</returns>
/// <remarks>
/// A delegate rather than a filesystem call inside the reader, because the ONE
/// way a cook rule may reach a byte is <c>IRuleContext</c>: reading a sidecar
/// <c>.bin</c> any other way would be an input the rule did not declare, and the
/// dependency set would then be smaller than the accessed set - which is a stale
/// artifact that looks correct.
/// </remarks>
public delegate byte[]? GltfBufferResolver(string contentPath);

/// <summary>
/// A managed glTF 2.0 and GLB reader: JSON through <c>Utf8JsonReader</c>, binary
/// through <c>BinaryPrimitives</c>, no reflection and no native library.
/// </summary>
/// <remarks>
/// <para><b>Hand-rolled, and the reason is COOK DETERMINISM rather than
/// dependency taste.</b> Assimp stays the runtime's loose-file importer and is
/// measured to run under NativeAOT (<c>docs/spikes/2026-09-cook-dependency-spikes.md</c>),
/// but it is a native library whose triangulation, welding and cache
/// optimisation are version-dependent: cooked bytes would then depend on which
/// machine cooked them, which is precisely what the three byte-identity oracles
/// exist to catch and precisely what they would be worst at explaining. Every
/// number this reader emits comes from the file's own bytes through arithmetic
/// written here.</para>
/// <para><b>What it does NOT do is guess.</b> A construct outside the supported
/// set is refused by name - the mode number and its glTF spelling, the extension
/// string, the component type - because the failure of guessing is not an
/// exception: it is an accessor walked at a stride the file never meant, which
/// produces a model that draws and is wrong. That is the same stance
/// <c>SimageReader</c> takes, and for the same reason it uses an allowlist rather
/// than a blocklist.</para>
/// <para><b>The node hierarchy is BAKED into the vertices and is then gone.</b> A
/// <c>.smodel</c> is one vertex buffer with no hierarchy section, so a transform
/// has to be spent at cook time; a mesh two nodes reference becomes two
/// submeshes, each already in the model's own space. That is what makes a cooked
/// model's bounds the same box the loose importer computes for the whole
/// hierarchy, and it is why a cooked prop instantiates as one node rather than as
/// the subtree the source file drew.</para>
/// <para><b>Two conversions are applied and both are properties of the SOURCE
/// FORMAT rather than options.</b> glTF puts v = 0 at the top of an image and
/// this engine samples v = 0 at the bottom, so v is flipped - the same flip
/// <c>ModelImportOptions.FlipTextureV</c> exists for, applied here always because
/// a glTF file always needs it. And a transform with a negative determinant
/// mirrors, so its triangles have their winding reversed, or a mirrored part of a
/// model renders inside out under backface culling with nothing reporting
/// it.</para>
/// </remarks>
public static class GltfReader
{
    /// <summary>The authored extensions this reader is asked for.</summary>
    public const string GltfExtension = ".gltf";

    /// <summary>The binary container's extension.</summary>
    public const string GlbExtension = ".glb";

    // "glTF" little-endian, the GLB header's first four bytes.
    private const uint GlbMagic = 0x46546C67;
    private const uint GlbJsonChunk = 0x4E4F534A;
    private const uint GlbBinaryChunk = 0x004E4942;
    private const int GlbHeaderSize = 12;
    private const int GlbChunkHeaderSize = 8;

    // Deep enough for any authored hierarchy and shallow enough that the walk
    // below cannot overflow the stack on a file built to make it. A cycle is
    // caught by the on-path marker instead; this catches the other shape, a
    // legal chain a hundred thousand nodes long.
    private const int MaxNodeDepth = 1024;

    private const int ComponentByte = 5120;
    private const int ComponentUnsignedByte = 5121;
    private const int ComponentShort = 5122;
    private const int ComponentUnsignedShort = 5123;
    private const int ComponentUnsignedInt = 5125;
    private const int ComponentFloat = 5126;

    private const int ModeTriangles = 4;

    private const string Base64Marker = ";base64,";

    /// <summary>Whether <paramref name="contentPath"/> is a file this reader takes.</summary>
    public static bool Handles(string contentPath)
    {
        ArgumentNullException.ThrowIfNull(contentPath);

        return contentPath.EndsWith(GltfExtension, StringComparison.OrdinalIgnoreCase)
            || contentPath.EndsWith(GlbExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads one glTF or GLB.
    /// </summary>
    /// <param name="file">The whole file.</param>
    /// <param name="source">
    /// What to call it in a message: a content path, so the same failure reads
    /// the same way from a project folder and from anywhere else.
    /// </param>
    /// <param name="resolveBuffer">
    /// How to fetch an external buffer, called with a content-relative path.
    /// </param>
    /// <exception cref="GltfFormatException">The file is not one this reader can carry.</exception>
    public static GltfModel Read(ReadOnlySpan<byte> file, string source, GltfBufferResolver resolveBuffer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolveBuffer);

        SplitContainer(file, source, out ReadOnlySpan<byte> json, out ReadOnlySpan<byte> binaryChunk);

        GltfDocument document = GltfDocument.Parse(json, source);
        RequireSupportedDocument(document, source);

        byte[][] buffers = ResolveBuffers(document, source, binaryChunk, resolveBuffer);
        return Build(document, source, buffers);
    }

    // ---- container ---------------------------------------------------------

    // A .gltf is JSON and a .glb is a 12-byte header over length-prefixed chunks.
    // Discriminated by the magic rather than by the extension, because the
    // extension is a name somebody typed and the magic is what the file IS - and
    // a .glb saved as .gltf is an ordinary mistake whose symptom otherwise is a
    // JSON parse failure at byte zero.
    private static void SplitContainer(
        ReadOnlySpan<byte> file, string source, out ReadOnlySpan<byte> json, out ReadOnlySpan<byte> binary)
    {
        json = file;
        binary = default;

        if (file.Length < 4) throw new GltfFormatException($"'{source}' is {file.Length} bytes, too short to be glTF.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(file) != GlbMagic) return;

        if (file.Length < GlbHeaderSize)
        {
            throw new GltfFormatException(
                $"'{source}' starts with the GLB magic and is {file.Length} bytes, too short to hold its " +
                $"{GlbHeaderSize}-byte header.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        if (version != 2)
        {
            throw new GltfFormatException(
                $"'{source}' is GLB container version {version}, and this reader implements version 2. " +
                "Re-export it as glTF 2.0.");
        }

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        if (declared > (uint)file.Length)
        {
            throw new GltfFormatException(
                $"'{source}' declares {declared} bytes and is {file.Length}. It is truncated.");
        }

        bool sawJson = false;
        int at = GlbHeaderSize;
        int end = (int)declared;

        while (at + GlbChunkHeaderSize <= end)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(file[at..]);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]);
            int body = at + GlbChunkHeaderSize;

            // Subtraction rather than addition: body + length is exactly the
            // arithmetic a corrupt file makes wrap, and a wrapped sum passes a
            // naive bound and then reads past the end of the buffer.
            if (length > (uint)(end - body))
            {
                throw new GltfFormatException(
                    $"'{source}' has a GLB chunk at byte {at} claiming {length} bytes, which runs past the " +
                    $"{end}-byte file.");
            }

            ReadOnlySpan<byte> payload = file.Slice(body, (int)length);
            if (type == GlbJsonChunk && !sawJson)
            {
                json = payload;
                sawJson = true;
            }
            else if (type == GlbBinaryChunk && binary.IsEmpty)
            {
                binary = payload;
            }

            // Every chunk is 4-byte aligned, padding included. Advancing by the
            // unpadded length reads the padding as the next chunk's header, which
            // is a plausible-looking length and a garbage type.
            at = body + (int)Align4(length);
        }

        if (!sawJson)
        {
            throw new GltfFormatException(
                $"'{source}' is a GLB with no JSON chunk, so there is no document in it.");
        }
    }

    private static uint Align4(uint value) => (value + 3u) & ~3u;

    // ---- what this reader will and will not carry --------------------------

    private static void RequireSupportedDocument(GltfDocument document, string source)
    {
        if (!document.AssetVersion.StartsWith("2.", StringComparison.Ordinal))
        {
            string stated = document.AssetVersion.Length == 0 ? "nothing" : $"'{document.AssetVersion}'";
            throw new GltfFormatException(
                $"'{source}' states asset version {stated}, and this reader implements glTF 2.0.");
        }

        // extensionsRequired is the file's own declaration that something in it
        // cannot be ignored - Draco compression, mesh quantization, a texture
        // transform. Skipping one would produce geometry that is silently wrong,
        // which is the whole class this reader refuses rather than guesses at.
        if (document.ExtensionsRequired.Count > 0)
        {
            throw new GltfFormatException(
                $"'{source}' requires the glTF extension(s) " +
                $"{string.Join(", ", document.ExtensionsRequired)}, which this reader does not implement. " +
                "Re-export without them.");
        }
    }

    // ---- buffers -----------------------------------------------------------

    private static byte[][] ResolveBuffers(
        GltfDocument document, string source, ReadOnlySpan<byte> binaryChunk, GltfBufferResolver resolveBuffer)
    {
        var buffers = new byte[document.Buffers.Count][];

        for (int i = 0; i < buffers.Length; i++)
        {
            GltfBufferJson buffer = document.Buffers[i];
            byte[] bytes;

            if (buffer.Uri is null)
            {
                // Only buffer 0 of a GLB may omit its uri, and it is the BIN
                // chunk. Copied rather than kept as a span, because everything
                // below reads buffers as arrays and a cook is not in the business
                // of shaving one copy off a file it is about to re-encode.
                if (i != 0 || binaryChunk.IsEmpty)
                {
                    throw new GltfFormatException(
                        $"'{source}' buffer {i} names no uri, which only the GLB binary chunk may do.");
                }

                bytes = binaryChunk.ToArray();
            }
            else if (buffer.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                bytes = DecodeDataUri(buffer.Uri, source, i);
            }
            else
            {
                string path = ResolveSiblingPath(source, buffer.Uri);
                bytes = resolveBuffer(path)
                    ?? throw new GltfFormatException(
                        $"'{source}' buffer {i} names '{buffer.Uri}', which resolves to '{path}' and is not " +
                        "in the content root.");
            }

            if (buffer.ByteLength > 0 && bytes.Length < buffer.ByteLength)
            {
                throw new GltfFormatException(
                    $"'{source}' buffer {i} declares {buffer.ByteLength} bytes and only {bytes.Length} " +
                    "arrived. It is truncated.");
            }

            buffers[i] = bytes;
        }

        return buffers;
    }

    private static byte[] DecodeDataUri(string uri, string source, int index)
    {
        int marker = uri.IndexOf(Base64Marker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            throw new GltfFormatException(
                $"'{source}' buffer {index} is a data uri that is not base64 encoded. Only " +
                "'data:...;base64,' is implemented, because a percent-encoded binary payload is not " +
                "something any exporter writes.");
        }

        try
        {
            return Convert.FromBase64String(uri[(marker + Base64Marker.Length)..]);
        }
        catch (FormatException ex)
        {
            throw new GltfFormatException(
                $"'{source}' buffer {index} carries base64 that does not decode: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Joins a glTF uri against the folder its model sits in, as a normalised
    /// content path.
    /// </summary>
    /// <remarks>
    /// <b>Its own segment walk rather than
    /// <c>ContentRoot.NormalizeRelativePath</c> alone</b>, because that function
    /// refuses <c>..</c> outright and a glTF uri legitimately carries one: a
    /// model in <c>Models/</c> naming <c>../Textures/x.png</c> is the ordinary
    /// export. So the <c>..</c> is resolved HERE, against the model's own folder,
    /// and an escape past the content root is then refused with the same words
    /// the normaliser would have used.
    /// </remarks>
    public static string ResolveSiblingPath(string modelContentPath, string uri)
    {
        ArgumentNullException.ThrowIfNull(modelContentPath);
        ArgumentNullException.ThrowIfNull(uri);

        // glTF uris are percent-encoded, so a file with a space in its name
        // arrives as %20 and would otherwise be looked for under that name.
        string decoded = Uri.UnescapeDataString(uri).Replace('\\', '/');

        var segments = new List<string>();
        foreach (string part in modelContentPath.Replace('\\', '/').Split('/'))
            segments.Add(part);

        // The model's own file name, which is a sibling rather than a folder.
        if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);

        foreach (string part in decoded.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;

            if (part == "..")
            {
                if (segments.Count == 0)
                {
                    throw new GltfFormatException(
                        $"'{modelContentPath}' names '{uri}', which escapes the content root.");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(part);
        }

        if (segments.Count == 0)
            throw new GltfFormatException($"'{modelContentPath}' names '{uri}', which is not a path.");

        return ContentRoot.NormalizeRelativePath(string.Join('/', segments));
    }

    // ---- geometry ----------------------------------------------------------

    private static GltfModel Build(GltfDocument document, string source, byte[][] buffers)
    {
        var submeshes = new List<GltfSubmesh>();
        var dropped = new SortedSet<string>(StringComparer.Ordinal);

        if (document.HasSkins) dropped.Add("skins (SKEL is designed and unwritten in .smodel v1)");
        if (document.HasAnimations) dropped.Add("animations (clips live in their own file, not in a mesh)");

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        // 0 unvisited, 1 on the current path. A node is returned to 0 when its
        // subtree is done rather than marked finished, so a node two parents
        // reference is emitted twice - which is what the file says - while a
        // cycle, which is a node reached while still on the path, is refused
        // before it can recurse forever.
        var onPath = new bool[document.Nodes.Count];

        foreach (int root in RootNodes(document, source))
            Visit(document, source, buffers, root, Matrix4x4.Identity, 0, onPath, submeshes, dropped);

        for (int i = 0; i < submeshes.Count; i++)
        {
            min = Vector3.Min(min, submeshes[i].BoundsMin);
            max = Vector3.Max(max, submeshes[i].BoundsMax);
        }

        if (submeshes.Count == 0)
        {
            throw new GltfFormatException(
                $"'{source}' holds no drawable triangles: its scene places no mesh, or every mesh it places " +
                "is empty.");
        }

        var materials = new GltfMaterial[document.Materials.Count];
        for (int i = 0; i < materials.Length; i++)
            materials[i] = new GltfMaterial(document.Materials[i].Name, BaseColorUri(document, i));

        return new GltfModel(submeshes, materials, min, max, [.. dropped]);
    }

    private static string? BaseColorUri(GltfDocument document, int material)
    {
        int? texture = document.Materials[material].BaseColorTexture;
        if (texture is not { } index || (uint)index >= (uint)document.TextureSources.Count) return null;

        int image = document.TextureSources[index];
        return (uint)image < (uint)document.ImageUris.Count ? document.ImageUris[image] : null;
    }

    private static IEnumerable<int> RootNodes(GltfDocument document, string source)
    {
        if (document.Scenes.Count > 0)
        {
            int index = document.DefaultScene ?? 0;
            if ((uint)index >= (uint)document.Scenes.Count)
            {
                throw new GltfFormatException(
                    $"'{source}' names default scene {index} and declares {document.Scenes.Count}.");
            }

            return document.Scenes[index];
        }

        // No scene at all is legal glTF and means the document is a library
        // rather than something to draw. Every node nothing claims as a child is
        // then a root, which is the reading that loses no geometry.
        var claimed = new bool[document.Nodes.Count];
        for (int i = 0; i < document.Nodes.Count; i++)
        {
            foreach (int child in document.Nodes[i].Children)
            {
                if ((uint)child < (uint)claimed.Length) claimed[child] = true;
            }
        }

        var roots = new List<int>();
        for (int i = 0; i < claimed.Length; i++)
        {
            if (!claimed[i]) roots.Add(i);
        }

        return roots;
    }

    private static void Visit(
        GltfDocument document,
        string source,
        byte[][] buffers,
        int index,
        Matrix4x4 parent,
        int depth,
        bool[] onPath,
        List<GltfSubmesh> submeshes,
        SortedSet<string> dropped)
    {
        if ((uint)index >= (uint)document.Nodes.Count)
        {
            throw new GltfFormatException(
                $"'{source}' places node {index} and declares {document.Nodes.Count}.");
        }

        if (onPath[index])
        {
            throw new GltfFormatException(
                $"'{source}' node {index} is its own ancestor. A glTF node hierarchy is a tree, and a walk " +
                "of a cyclic one never ends.");
        }

        if (depth >= MaxNodeDepth)
        {
            throw new GltfFormatException(
                $"'{source}' nests nodes more than {MaxNodeDepth} deep, which no authored hierarchy does.");
        }

        GltfNodeJson node = document.Nodes[index];
        Matrix4x4 world = LocalMatrix(node, source, index) * parent;

        onPath[index] = true;

        if (node.Mesh is { } mesh)
            AddMesh(document, source, buffers, mesh, node, world, submeshes, dropped);

        foreach (int child in node.Children)
            Visit(document, source, buffers, child, world, depth + 1, onPath, submeshes, dropped);

        onPath[index] = false;
    }

    /// <summary>
    /// One node's transform, in the engine's row-vector convention.
    /// </summary>
    /// <remarks>
    /// <b>The two spellings both need converting and they need converting
    /// differently.</b> glTF stores a matrix COLUMN-major for column vectors, so
    /// reading its sixteen floats into <c>Matrix4x4</c>'s row-major fields in
    /// order is exactly the transpose the row-vector convention wants - no
    /// explicit transpose, and writing one would undo it. The TRS form composes
    /// as <c>T * R * S</c> for column vectors, which reverses to
    /// <c>S * R * T</c> here. Getting either wrong puts a part of a model
    /// somewhere nobody asked for, which is the classic symptom this repo already
    /// records for the importer.
    /// </remarks>
    private static Matrix4x4 LocalMatrix(GltfNodeJson node, string source, int index)
    {
        if (node.Matrix is not { } m) return
            Matrix4x4.CreateScale(node.Scale)
            * Matrix4x4.CreateFromQuaternion(node.Rotation)
            * Matrix4x4.CreateTranslation(node.Translation);

        if (node.HasTrs)
        {
            throw new GltfFormatException(
                $"'{source}' node {index} carries both a matrix and a translation, rotation or scale. glTF " +
                "forbids that, and the two disagree about where the node is.");
        }

        return new Matrix4x4(
            m[0], m[1], m[2], m[3],
            m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11],
            m[12], m[13], m[14], m[15]);
    }

    private static void AddMesh(
        GltfDocument document,
        string source,
        byte[][] buffers,
        int meshIndex,
        GltfNodeJson node,
        Matrix4x4 world,
        List<GltfSubmesh> submeshes,
        SortedSet<string> dropped)
    {
        if ((uint)meshIndex >= (uint)document.Meshes.Count)
        {
            throw new GltfFormatException(
                $"'{source}' node '{node.Name}' names mesh {meshIndex} and the file declares " +
                $"{document.Meshes.Count}.");
        }

        GltfMeshJson mesh = document.Meshes[meshIndex];
        for (int i = 0; i < mesh.Primitives.Count; i++)
        {
            GltfPrimitiveJson primitive = mesh.Primitives[i];

            if (primitive.Mode != ModeTriangles)
            {
                throw new GltfFormatException(
                    $"'{source}' mesh '{mesh.Name}' primitive {i} is mode {primitive.Mode} " +
                    $"({DescribeMode(primitive.Mode)}), and this cook writes triangles only. Re-export it " +
                    "triangulated.");
            }

            foreach (string attribute in primitive.OtherAttributes)
                dropped.Add($"vertex attribute {attribute}");

            if (primitive.HasMorphTargets) dropped.Add("morph targets");
            if (primitive.TexCoord0 is null) dropped.Add("texture coordinates (none in the file; written zero)");

            string name = mesh.Name.Length > 0 ? mesh.Name : node.Name;
            submeshes.Add(BuildSubmesh(
                document, source, buffers, primitive, $"{name}[{i}]", world));
        }
    }

    private static GltfSubmesh BuildSubmesh(
        GltfDocument document,
        string source,
        byte[][] buffers,
        GltfPrimitiveJson primitive,
        string name,
        Matrix4x4 world)
    {
        if (primitive.Position is not { } positionAccessor)
        {
            throw new GltfFormatException(
                $"'{source}' primitive '{name}' declares no POSITION attribute, so there is nothing to draw.");
        }

        float[] positions = ReadFloatAccessor(document, source, buffers, positionAccessor, 3, "POSITION");
        int vertexCount = positions.Length / 3;

        float[]? normals = primitive.Normal is { } normalAccessor
            ? ReadFloatAccessor(document, source, buffers, normalAccessor, 3, "NORMAL")
            : null;

        float[]? uvs = primitive.TexCoord0 is { } uvAccessor
            ? ReadFloatAccessor(document, source, buffers, uvAccessor, 2, "TEXCOORD_0")
            : null;

        RequireMatchingCount(source, name, "NORMAL", normals, 3, vertexCount);
        RequireMatchingCount(source, name, "TEXCOORD_0", uvs, 2, vertexCount);

        uint[] indices = primitive.Indices is { } indexAccessor
            ? ReadIndexAccessor(document, source, buffers, indexAccessor, vertexCount, name)
            : Sequential(vertexCount);

        if (indices.Length % 3 != 0)
        {
            throw new GltfFormatException(
                $"'{source}' primitive '{name}' has {indices.Length} indices, which is not a whole number of " +
                "triangles.");
        }

        // A transform whose determinant is negative mirrors, and a mirrored
        // triangle keeps its index order while its geometric winding reverses -
        // so it renders inside out under backface culling, with nothing anywhere
        // reporting it.
        bool mirrored = world.GetDeterminant() < 0f;
        Matrix4x4 normalMatrix = NormalMatrix(world);

        if (normals is null)
        {
            // The glTF specification's own rule for a primitive with no normals:
            // flat, per face. That needs one vertex per corner, so the primitive
            // is expanded here rather than smoothed - smoothing would need a weld
            // by position, which is the importer's business and would make the
            // cooked model differ from the file for a reason the file did not
            // state.
            return FlatShaded(positions, uvs, indices, name, primitive.Material ?? -1, world, mirrored);
        }

        var vertices = new float[vertexCount * 8];
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        for (int v = 0; v < vertexCount; v++)
        {
            var position = Vector3.Transform(
                new Vector3(positions[v * 3], positions[(v * 3) + 1], positions[(v * 3) + 2]), world);

            Vector3 normal = TransformNormal(
                new Vector3(normals[v * 3], normals[(v * 3) + 1], normals[(v * 3) + 2]), normalMatrix);

            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);

            int at = v * 8;
            vertices[at] = position.X;
            vertices[at + 1] = position.Y;
            vertices[at + 2] = position.Z;
            vertices[at + 3] = normal.X;
            vertices[at + 4] = normal.Y;
            vertices[at + 5] = normal.Z;
            vertices[at + 6] = uvs is null ? 0f : uvs[v * 2];
            vertices[at + 7] = uvs is null ? 0f : FlipV(uvs[(v * 2) + 1]);
        }

        if (mirrored) ReverseWinding(indices);

        return new GltfSubmesh(name, primitive.Material ?? -1, vertices, indices, min, max);
    }

    private static GltfSubmesh FlatShaded(
        float[] positions,
        float[]? uvs,
        uint[] indices,
        string name,
        int material,
        Matrix4x4 world,
        bool mirrored)
    {
        var vertices = new float[indices.Length * 8];
        var expanded = new uint[indices.Length];
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        // Outside the loop, because a stackalloc inside one is not freed per
        // iteration: it accumulates for the whole call, which for a mesh of any
        // size is a stack overflow rather than an exception.
        Span<Vector3> corner = stackalloc Vector3[3];

        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            for (int c = 0; c < 3; c++)
            {
                uint index = indices[triangle + c];
                corner[c] = Vector3.Transform(
                    new Vector3(positions[index * 3], positions[(index * 3) + 1], positions[(index * 3) + 2]),
                    world);
            }

            // Computed AFTER the transform, so it is the normal of the triangle
            // as it actually sits, mirroring included, rather than the source
            // normal pushed through a matrix.
            Vector3 face = Vector3.Cross(corner[1] - corner[0], corner[2] - corner[0]);
            face = face.LengthSquared() > 0f ? Vector3.Normalize(face) : Vector3.UnitY;
            if (mirrored) face = -face;

            for (int c = 0; c < 3; c++)
            {
                int v = triangle + c;
                uint index = indices[v];

                min = Vector3.Min(min, corner[c]);
                max = Vector3.Max(max, corner[c]);

                int at = v * 8;
                vertices[at] = corner[c].X;
                vertices[at + 1] = corner[c].Y;
                vertices[at + 2] = corner[c].Z;
                vertices[at + 3] = face.X;
                vertices[at + 4] = face.Y;
                vertices[at + 5] = face.Z;
                vertices[at + 6] = uvs is null ? 0f : uvs[index * 2];
                vertices[at + 7] = uvs is null ? 0f : FlipV(uvs[(index * 2) + 1]);
                expanded[v] = (uint)v;
            }
        }

        if (mirrored) ReverseWinding(expanded);

        return new GltfSubmesh(name, material, vertices, expanded, min, max);
    }

    // v = 0 is the BOTTOM of an image in this engine and the TOP in glTF, so
    // every glTF UV needs this. Named rather than written inline at its two call
    // sites, because a flip applied in one of them and not the other is a model
    // whose flat-shaded primitives are mirrored and whose smooth ones are not.
    private static float FlipV(float v) => 1f - v;

    private static void ReverseWinding(uint[] indices)
    {
        for (int i = 0; i + 2 < indices.Length; i += 3)
            (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
    }

    // The inverse transpose, which is what a normal transforms by under a
    // non-uniform scale. A singular matrix has no inverse and also has no
    // meaningful normals, so the transform itself stands in and the normalise
    // below cleans up whatever comes out.
    private static Matrix4x4 NormalMatrix(Matrix4x4 world) =>
        Matrix4x4.Invert(world, out Matrix4x4 inverse) ? Matrix4x4.Transpose(inverse) : world;

    private static Vector3 TransformNormal(Vector3 normal, Matrix4x4 matrix)
    {
        Vector3 transformed = Vector3.TransformNormal(normal, matrix);
        return transformed.LengthSquared() > 0f ? Vector3.Normalize(transformed) : Vector3.UnitY;
    }

    private static uint[] Sequential(int count)
    {
        var indices = new uint[count];
        for (int i = 0; i < count; i++) indices[i] = (uint)i;
        return indices;
    }

    private static void RequireMatchingCount(
        string source, string name, string attribute, float[]? values, int components, int vertexCount)
    {
        if (values is null || values.Length / components == vertexCount) return;

        throw new GltfFormatException(
            $"'{source}' primitive '{name}' has {vertexCount} positions and {values.Length / components} " +
            $"{attribute} values. glTF requires every attribute of a primitive to have the same count.");
    }

    // ---- accessors ---------------------------------------------------------

    private static float[] ReadFloatAccessor(
        GltfDocument document, string source, byte[][] buffers, int index, int components, string what)
    {
        GltfAccessorJson accessor = RequireAccessor(document, source, index, what);

        if (accessor.ComponentType != ComponentFloat)
        {
            throw new GltfFormatException(
                $"'{source}' accessor {index} ({what}) has component type " +
                $"{DescribeComponentType(accessor.ComponentType)}, and this cook reads {what} as 32-bit " +
                "float. Re-export without quantization.");
        }

        if (accessor.Normalized)
        {
            throw new GltfFormatException(
                $"'{source}' accessor {index} ({what}) is marked normalized, which is only meaningful for " +
                "integer components and is not implemented here.");
        }

        int declared = ComponentCount(accessor.Type);
        if (declared != components)
        {
            throw new GltfFormatException(
                $"'{source}' accessor {index} ({what}) is {accessor.Type} and this cook needs " +
                $"{components} components.");
        }

        int elementSize = components * sizeof(float);
        LocateAccessor(
            document, source, buffers, accessor, index, elementSize,
            out byte[] buffer, out int start, out int stride);

        var values = new float[accessor.Count * components];
        for (int element = 0; element < accessor.Count; element++)
        {
            ReadOnlySpan<byte> payload = buffer.AsSpan(start + (element * stride), elementSize);
            for (int c = 0; c < components; c++)
            {
                values[(element * components) + c] =
                    BinaryPrimitives.ReadSingleLittleEndian(payload[(c * sizeof(float))..]);
            }
        }

        return values;
    }

    private static uint[] ReadIndexAccessor(
        GltfDocument document, string source, byte[][] buffers, int index, int vertexCount, string name)
    {
        GltfAccessorJson accessor = RequireAccessor(document, source, index, "indices");

        if (ComponentCount(accessor.Type) != 1)
        {
            throw new GltfFormatException(
                $"'{source}' accessor {index} is {accessor.Type} and an index accessor must be SCALAR.");
        }

        int width = accessor.ComponentType switch
        {
            ComponentUnsignedByte => 1,
            ComponentUnsignedShort => 2,
            ComponentUnsignedInt => 4,

            // An allowlist, not a blocklist. A signed index type is not a thing
            // glTF permits, and reading one anyway would turn a negative value
            // into a very large vertex index rather than into an error.
            _ => throw new GltfFormatException(
                $"'{source}' accessor {index} has index component type " +
                $"{DescribeComponentType(accessor.ComponentType)}. glTF allows unsigned byte, unsigned " +
                "short and unsigned int."),
        };

        LocateAccessor(
            document, source, buffers, accessor, index, width,
            out byte[] buffer, out int start, out int stride);

        var indices = new uint[accessor.Count];
        for (int element = 0; element < accessor.Count; element++)
        {
            ReadOnlySpan<byte> payload = buffer.AsSpan(start + (element * stride), width);
            indices[element] = width switch
            {
                1 => payload[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(payload),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(payload),
            };
        }

        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] < (uint)vertexCount) continue;

            throw new GltfFormatException(
                $"'{source}' primitive '{name}' index {i} names vertex {indices[i]}, and the primitive has " +
                $"{vertexCount}.");
        }

        return indices;
    }

    private static GltfAccessorJson RequireAccessor(
        GltfDocument document, string source, int index, string what)
    {
        if ((uint)index >= (uint)document.Accessors.Count)
        {
            throw new GltfFormatException(
                $"'{source}' names accessor {index} for {what} and declares {document.Accessors.Count}.");
        }

        GltfAccessorJson accessor = document.Accessors[index];

        if (accessor.Sparse)
        {
            throw new GltfFormatException(
                $"'{source}' accessor {index} ({what}) is sparse, which this cook does not implement. " +
                "Reading only its base array would drop exactly the values a sparse accessor exists to " +
                "carry, so it is refused rather than half read.");
        }

        if (accessor.Count <= 0)
        {
            throw new GltfFormatException(
                $"'{source}' accessor {index} ({what}) declares {accessor.Count} elements.");
        }

        return accessor;
    }

    // Validates one accessor's whole span and hands back where its elements live.
    // The stride is the bufferView's when it states one - an interleaved buffer is
    // ordinary glTF - and the element's own size otherwise, and every bound is
    // checked HERE, once, before the first read: a per-element check inside the
    // two callers would be two copies of the arithmetic that decides whether this
    // reader indexes past the end of a buffer.
    private static void LocateAccessor(
        GltfDocument document,
        string source,
        byte[][] buffers,
        GltfAccessorJson accessor,
        int accessorIndex,
        int elementSize,
        out byte[] elements,
        out int start,
        out int stride)
    {
        if (accessor.BufferView is not { } viewIndex)
        {
            throw new GltfFormatException(
                $"'{source}' accessor {accessorIndex} names no bufferView. An accessor without one reads as " +
                "zeros, which is only meaningful under a sparse accessor and this cook refuses those.");
        }

        if ((uint)viewIndex >= (uint)document.BufferViews.Count)
        {
            throw new GltfFormatException(
                $"'{source}' accessor {accessorIndex} names bufferView {viewIndex} and the file declares " +
                $"{document.BufferViews.Count}.");
        }

        GltfBufferViewJson view = document.BufferViews[viewIndex];
        if ((uint)view.Buffer >= (uint)buffers.Length)
        {
            throw new GltfFormatException(
                $"'{source}' bufferView {viewIndex} names buffer {view.Buffer} and the file declares " +
                $"{buffers.Length}.");
        }

        byte[] buffer = buffers[view.Buffer];
        if (view.ByteOffset < 0 || view.ByteLength < 0
            || view.ByteOffset > buffer.Length || view.ByteLength > buffer.Length - view.ByteOffset)
        {
            throw new GltfFormatException(
                $"'{source}' bufferView {viewIndex} claims {view.ByteLength} bytes at offset " +
                $"{view.ByteOffset} of a {buffer.Length}-byte buffer.");
        }

        stride = view.ByteStride > 0 ? view.ByteStride : elementSize;
        if (stride < elementSize)
        {
            throw new GltfFormatException(
                $"'{source}' bufferView {viewIndex} states a stride of {stride} bytes for elements that are " +
                $"{elementSize}.");
        }

        long last = (long)accessor.ByteOffset + ((long)(accessor.Count - 1) * stride) + elementSize;
        if (accessor.ByteOffset < 0 || last > view.ByteLength)
        {
            throw new GltfFormatException(
                $"'{source}' accessor {accessorIndex} reads to byte {last} of a {view.ByteLength}-byte " +
                "bufferView.");
        }

        elements = buffer;
        start = view.ByteOffset + accessor.ByteOffset;
    }

    private static int ComponentCount(string type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        _ => -1,
    };

    private static string DescribeComponentType(int componentType) => componentType switch
    {
        ComponentByte => "5120 (BYTE)",
        ComponentUnsignedByte => "5121 (UNSIGNED_BYTE)",
        ComponentShort => "5122 (SHORT)",
        ComponentUnsignedShort => "5123 (UNSIGNED_SHORT)",
        ComponentUnsignedInt => "5125 (UNSIGNED_INT)",
        ComponentFloat => "5126 (FLOAT)",
        _ => $"{componentType} (unknown)",
    };

    private static string DescribeMode(int mode) => mode switch
    {
        0 => "POINTS",
        1 => "LINES",
        2 => "LINE_LOOP",
        3 => "LINE_STRIP",
        4 => "TRIANGLES",
        5 => "TRIANGLE_STRIP",
        6 => "TRIANGLE_FAN",
        _ => "unknown",
    };
}
