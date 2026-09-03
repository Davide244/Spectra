using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Models;
using SpectraEngine.Core.Assets.Models;
using SpectraEngine.Core.Assets.Packs;
using System;
using System.Collections.Generic;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// Turns an authored glTF or GLB into a <c>.smodel</c>: one vertex buffer, one
/// index buffer, submeshes as index ranges, materials named by path.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this buys at runtime is that a shipped game imports nothing.</b> A
/// loose model costs a JSON parse, an accessor walk per attribute, a
/// de-interleave, a triangulation and - through <c>ModelImporter</c> - a 5.8 MB
/// native library in the payload; the cooked form arrives as the exact float
/// array <c>CreateMesh</c> takes, already in the engine's own layout, already in
/// the model's own space. What it does not buy is a copy-free upload: see
/// <c>CookedModelData</c> for why the loader still gathers per submesh, and for
/// what would remove that.
/// </para>
/// <para>
/// <b>The node hierarchy is spent at cook time and the model comes out
/// FLAT.</b> A <c>.smodel</c> has one vertex buffer and no hierarchy section, so
/// each node's accumulated transform is baked into the vertices it places and a
/// mesh two nodes reference becomes two submeshes. The cooked model's bounds are
/// then the same box the loose importer computes over its whole hierarchy, and
/// the visible difference is that instantiating a cooked prop produces one node
/// rather than the subtree the source file drew. That is stated rather than
/// hidden, because it is the one thing about a cooked model that is not simply
/// the loose one arriving faster.
/// </para>
/// <para>
/// <b>A material reference is a PATH the cook resolved, and a model that names
/// no authored material says so.</b> <c>SUBM</c> stores a logical asset path, so
/// the only thing a cooked submesh can point at is a <c>.spectramat</c> that
/// exists - and an exporter writes its surface inline, as a base colour texture
/// and a factor, which the format has no field for. The lookup is
/// <see cref="ModelMaterialOverride"/>, the SAME function
/// <c>AssetManager</c> asks at load, so a cooked reference and a loose override
/// cannot be two different files. What is missing is reported as SC3002, soft,
/// because the author's model is valid and the limitation is the format's.
/// </para>
/// <para>
/// <b>Every read and every probe goes through <see cref="IRuleContext"/>,
/// sidecar buffers included.</b> A <c>.gltf</c> beside a <c>.bin</c> is the
/// ordinary export, and reading that <c>.bin</c> any other way would be an input
/// the rule did not declare - so the dependency set would be smaller than the
/// accessed set, and editing the geometry would not re-cook the model. The
/// material probes are recorded for the mirror reason: authoring the
/// <c>.spectramat</c> that was missing has to re-cook exactly the models that
/// looked for it.
/// </para>
/// <para>
/// <b>A file the reader refuses is reported and emits nothing.</b> Falling back
/// to a raw copy would be worse than failing: the pack would carry a broken glTF
/// under a path the engine resolves, a shipped build would hand it to an importer
/// it does not link, and the build log would say a model cooked.
/// </para>
/// </remarks>
public sealed class ModelRule : IRule
{
    /// <inheritdoc/>
    public RuleKind Kind => RuleKind.Model;

    /// <inheritdoc/>
    /// <remarks>
    /// Raise this whenever the bytes this rule emits for one source can change: a
    /// different vertex layout, a change to the container, a different rule for
    /// which node transforms are baked. <c>EngineInfo.ModelFormatVersion</c> and
    /// <c>GeometryFormatVersion</c> are not covered by it - a reader enforces
    /// those instead.
    /// </remarks>
    public int Version => 1;

    /// <inheritdoc/>
    /// <remarks>
    /// None, and it is a real answer. Geometry does not vary with the profile:
    /// there is no search here, no quality knob and nothing to trade, so
    /// declaring the profile would re-cook a project's whole prop library on a
    /// <c>--profile fast</c> run for bytes that cannot differ. It does not vary
    /// with the target list either, since a vertex buffer is the same buffer on
    /// every backend.
    /// </remarks>
    public CookSettingKeys SettingsRead => CookSettingKeys.None;

    /// <summary>Whether <paramref name="contentPath"/> is a model this rule cooks.</summary>
    public static bool Handles(string contentPath) => GltfReader.Handles(contentPath);

    /// <inheritdoc/>
    public void Cook(IRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        byte[] source = context.Read(context.SourcePath);

        GltfModel model;
        try
        {
            model = GltfReader.Read(source, context.SourcePath, path => ReadBuffer(context, path));
        }
        catch (GltfFormatException ex)
        {
            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.ModelUndecodable,
                $"'{context.SourcePath}' could not be read: {ex.Message}",
                context.SourcePath));

            return;
        }

        foreach (string dropped in model.Dropped)
        {
            context.Report(CookDiagnostic.Info(
                CookDiagnosticCodes.ModelDataDropped,
                $"'{context.SourcePath}' carries {dropped}, which a v1 .smodel does not.",
                context.SourcePath));
        }

        string?[] materials = ResolveMaterials(context, model);

        var vertices = new List<float>();
        var indices = new List<uint>();
        var submeshes = new List<SmodelSubmeshSpec>(model.Submeshes.Count);

        for (int i = 0; i < model.Submeshes.Count; i++)
        {
            GltfSubmesh submesh = model.Submeshes[i];
            var start = (uint)indices.Count;
            var vertexBase = (uint)(vertices.Count / (int)SmodelStandardLayout.StrideFloats);

            vertices.AddRange(submesh.Vertices);
            for (int at = 0; at < submesh.Indices.Length; at++)
                indices.Add(submesh.Indices[at] + vertexBase);

            // Every submesh is a contiguous run of both buffers, which is not a
            // property the FORMAT promises and is what makes the loader's slice
            // exact rather than merely correct. Recorded here rather than
            // asserted at load, where a file another cooker wrote would fail an
            // assertion it never agreed to.
            submeshes.Add(new SmodelSubmeshSpec(
                start,
                (uint)submesh.Indices.Length,
                (uint)submesh.MaterialIndex < (uint)materials.Length
                    ? materials[submesh.MaterialIndex]
                    : null));
        }

        byte[] cooked;
        try
        {
            cooked = SmodelWriter.Write(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices),
                submeshes);
        }
        catch (ArgumentException ex)
        {
            // The writer measures everything it is handed against the format's
            // own limits, so this is the reader producing something the container
            // cannot hold. Reported rather than thrown, because a cook must name
            // the asset that broke rather than stopping at SC1004.
            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.ModelEncodeFailed,
                $"'{context.SourcePath}' produced a model the container cannot hold: {ex.Message}",
                context.SourcePath));

            return;
        }

        context.Emit(ModelContentPath.CookedPathFor(context.SourcePath), cooked, PackEntryKind.Model);
    }

    // The rule's own view of a sidecar buffer. Probed before the read rather than
    // catching the miss, because RuleInputMissingException is how a rule STOPS
    // and this one wants to report SC3001 naming the buffer instead. Both calls
    // record, and the context folds the pair into one dependency.
    private static byte[]? ReadBuffer(IRuleContext context, string contentPath) =>
        context.Probe(contentPath) ? context.Read(contentPath) : null;

    // One path per material slot, index-aligned with the file's own table. Only
    // slots a submesh actually draws with are looked up: an exporter routinely
    // emits materials nothing references, and reporting those as unauthored would
    // ask an author to write files for surfaces that are not in the model.
    private static string?[] ResolveMaterials(IRuleContext context, GltfModel model)
    {
        var paths = new string?[model.Materials.Count];

        Span<bool> referenced = model.Materials.Count <= 64
            ? stackalloc bool[model.Materials.Count]
            : new bool[model.Materials.Count];
        referenced.Clear();

        for (int i = 0; i < model.Submeshes.Count; i++)
        {
            int index = model.Submeshes[i].MaterialIndex;
            if ((uint)index < (uint)referenced.Length) referenced[index] = true;
        }

        for (int i = 0; i < paths.Length; i++)
        {
            if (!referenced[i]) continue;

            GltfMaterial material = model.Materials[i];
            string? candidate = ModelMaterialOverride.PathFor(material.Name);

            if (candidate is not null && context.Probe(candidate))
            {
                paths[i] = candidate;
                continue;
            }

            context.Report(CookDiagnostic.Warning(
                CookDiagnosticCodes.ModelMaterialUnauthored,
                Describe(context.SourcePath, material, candidate),
                context.SourcePath));
        }

        return paths;
    }

    private static string Describe(string sourcePath, in GltfMaterial material, string? candidate)
    {
        string named = material.Name.Length > 0 ? $"material '{material.Name}'" : "an unnamed material";
        string wanted = candidate is null
            ? $"and a cooked model can only reference a material by path, so there is nothing to name. " +
              $"Give it a name and author '{ModelMaterialOverride.Folder}/<name>" +
              $"{SpectraEngine.Core.Assets.MaterialParser.FileExtension}'"
            : $"and this project has no '{candidate}'. Author one";

        string texture = material.BaseColorImageUri is { Length: > 0 } uri
            ? $" The file's own base colour image is '{uri}'."
            : string.Empty;

        return $"'{sourcePath}' draws with {named} {wanted}, or the cooked submesh binds the engine's " +
            $"default material where a loose import would have used what the file describes.{texture}";
    }
}
