using System;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// What a gameplay ray hit: where, what surface, and which node if any.
/// </summary>
/// <remarks>
/// <see cref="Node"/> is null for static world geometry. That is not an
/// omission: the static world is fused from many brushes into per-chunk
/// surfaces, so "which node did I hit" has no single answer once two brushes
/// have merged along a shared face. A part brush or a mesh node reports itself.
/// </remarks>
public readonly record struct GameplayRayHit(
    SceneNode? Node,
    Vector3 Point,
    Vector3 Normal,
    float Distance,
    MaterialRef Material,
    bool StaticWorld);

public sealed partial class Scene
{
    /// <summary>
    /// The gameplay ray: what a shot, a footstep probe or a line-of-sight check
    /// should ask. Reports the first solid surface along the ray, in agreement
    /// with what is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the query that answers about the world you can SEE, which is
    /// what makes it different from <see cref="Raycast(in Ray3, out SceneRaycastHit, float)"/>.</b>
    /// That one tests each brush against its authored planes, so it reports
    /// solid in the middle of a doorway that a subtractive brush opened: correct
    /// as authored-geometry authority, wrong as a gameplay answer. A shot fired
    /// through a door would stop in mid-air.
    /// </para>
    /// <para>
    /// <b>Two lanes, and neither can be dropped.</b> World geometry comes from
    /// the compiled static world, which is the only structure that knows what
    /// the carve removed. Part brushes and mesh nodes come live from the spatial
    /// index, because they are deliberately absent from that compile. The result
    /// is whichever is nearer. The BVH pass is told to skip world brushes so the
    /// same geometry is never answered twice, once correctly and once not.
    /// </para>
    /// <para>
    /// <b>What it does not yet cover:</b> dynamic rigid bodies, which do not
    /// exist yet. When they do, a physics backend composes its own broadphase
    /// with this rather than replacing it, because the static world and part
    /// brushes need no physics backend to be queryable and a dedicated server
    /// or an editor must keep working without one.
    /// </para>
    /// </remarks>
    public bool RaycastGameplay(
        in Ray3 ray,
        out GameplayRayHit hit,
        float maxDistance = 1000f)
        => RaycastGameplay(in ray, out hit, default, maxDistance);

    /// <inheritdoc cref="RaycastGameplay(in Ray3, out GameplayRayHit, float)"/>
    public bool RaycastGameplay(
        in Ray3 ray,
        out GameplayRayHit hit,
        in SceneQueryFilter filter,
        float maxDistance = 1000f)
    {
        hit = default;

        if (ray.Direction == Vector3.Zero || !(maxDistance > 0f))
            return false;

        Vector3 direction = Vector3.Normalize(ray.Direction);
        bool found = false;
        float best = maxDistance;

        // --- Lane 1: the compiled world -----------------------------------
        if (StaticWorld is { } world &&
            world.Raycast(ray.Origin, direction, maxDistance, out BspRaycastHit worldHit))
        {
            found = true;
            best = worldHit.Distance;

            // The BSP reports the plane it crossed, not the polygon that lies on
            // it, so the material is resolved afterwards from the owning chunk's
            // surfaces. Bounded by the surfaces in one 32-unit cell, and exact:
            // the hit point lies on exactly one of them.
            MaterialRef material = world.TryResolveSurface(worldHit.Point, worldHit.Normal, out FaceSurface face)
                ? face.Material
                : default;

            hit = new GameplayRayHit(null, worldHit.Point, worldHit.Normal, worldHit.Distance, material, true);
        }

        // --- Lane 2: parts and meshes, live -------------------------------
        // Bounded by the world hit: nothing behind it can win, and handing the
        // shorter distance to the BVH lets it reject whole subtrees.
        SceneQueryFilter sceneFilter = filter with { ExcludeStaticWorldBrushes = true };
        if (Raycast(in ray, out SceneRaycastHit nodeHit, in sceneFilter, best) && nodeHit.Distance <= best)
        {
            MaterialRef material = ResolveNodeMaterial(nodeHit.Node, nodeHit.Normal);
            hit = new GameplayRayHit(
                nodeHit.Node, nodeHit.Point, nodeHit.Normal, nodeHit.Distance, material, false);
            found = true;
        }

        return found;
    }

    // A part brush's material comes from the face whose plane the hit normal
    // matches. Mesh nodes have no per-face material concept here, so they report
    // the default and the caller reads Node instead.
    private static MaterialRef ResolveNodeMaterial(SceneNode node, Vector3 normal)
    {
        if (node.Brush is not { } brush)
            return default;

        Matrix4x4 world = node.WorldMatrix;
        int best = -1;
        float bestAgreement = 0.9f;

        for (int i = 0; i < brush.LocalPlanes.Count; i++)
        {
            Vector3 worldNormal = Vector3.Normalize(
                Vector3.TransformNormal(brush.LocalPlanes[i].Normal, world));

            float agreement = Vector3.Dot(worldNormal, normal);
            if (agreement > bestAgreement)
            {
                bestAgreement = agreement;
                best = i;
            }
        }

        return best >= 0 ? brush.FaceSurfaces[best].Material : default;
    }
}
