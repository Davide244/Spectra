using System;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// A human-scale obstacle course, authored beside the demo room, that exists so
/// the character controller can be <em>walked</em> rather than only asserted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is a separate place rather than a bigger demo room.</b> The
/// original room is 6×6 spectraunits with its floor top at y = −1 and its
/// doorway opening 1.2 units tall. A 1.8-unit character cannot stand in it, let
/// alone walk through that door — and the door must stay exactly as it is,
/// because its ±z planes coincide with the wall's and it is therefore the
/// engine's flush-coplanar-cut regression fixture. Enlarging it to make it
/// walkable would delete the test. So the course is authored at
/// <see cref="Center"/>, clear of both the room and the ±100 scatter field, and
/// nothing about the original demo changes.
/// </para>
/// <para>
/// <b>Every feature here fails visibly rather than subtly.</b> Each one targets
/// a specific claim the mover makes, and each is sized so that a regression is
/// something you walk into rather than something you have to measure: stairs at
/// three rises that bracket <see cref="Character.CharacterTuning.StepHeight"/>
/// (two must climb, one must refuse), ramps at three angles that bracket
/// <see cref="Character.CharacterTuning.MaxSlopeAngleDegrees"/>, a doorway and a
/// tunnel cut by subtractive brushes — which is the whole reason the collision
/// source is a plane set and not a convex hull per brush — a chasm wide enough
/// to need a jump and bottomless enough to reach the fall-out guard, and a part
/// brush to stand on, which is the only geometry here that comes down the live
/// lane instead of out of the compiled world.
/// </para>
/// <para>
/// Coordinates are absolute and hand-placed rather than parameterised. A course
/// whose pieces move when a constant changes is a course whose regressions move
/// too, and the numbers below are chosen against the tuning defaults — they are
/// the test, so they are written where they can be read.
/// </para>
/// </remarks>
public static class DemoPlayArea
{
    /// <summary>Where the course sits, clear of the demo room and the scatter field.</summary>
    /// <remarks>
    /// The scattered parts cover ±100 units around the origin, so x = 150 is
    /// clear of them by 20 units at the course's western edge. Far enough out to
    /// exercise chunked compilation away from the origin (this is four to five
    /// 32-unit cells from it), close enough that a stats line's chunk counts
    /// stay legible.
    /// </remarks>
    public static readonly Vector3 Center = new(150f, 0f, 0f);

    /// <summary>Where the character stands when play mode begins: on the floor, at the west end.</summary>
    /// <remarks>
    /// Feet a hair above the slab rather than exactly on it. The mover's first
    /// tick resolves the millimetre with a ground snap, which is a cheaper and
    /// more honest start than spawning a capsule flush with a surface and
    /// depending on the depenetration pass to like it.
    /// </remarks>
    public static readonly Vector3 Spawn = new(133f, 0.05f, 0f);

    /// <summary>The yaw the character starts with: looking east, down the course.</summary>
    /// <remarks>
    /// Zero is +x in this engine's convention (<c>forward = (cos yaw, 0, sin
    /// yaw)</c>), which the mover and the camera share — so a spawn facing the
    /// first obstacle costs no angle at all.
    /// </remarks>
    public const float SpawnYaw = 0f;

    /// <summary>Below this the character has left the course and is respawned.</summary>
    /// <remarks>
    /// The floor's underside is at y = −3 and there is nothing beneath it, so
    /// anything past −20 is falling and will keep falling. The only way to get
    /// there is the chasm — the perimeter walls close every other exit — which
    /// is what makes this guard a thing a player meets rather than dead code.
    /// </remarks>
    public const float FallOutHeight = -20f;

    // The floor slab is deliberately three units THICK rather than the demo
    // room's 0.2. It is what lets the trench below be cut straight into it: a
    // pit carved through a thin slab opens into the void underneath, and a
    // character that drops in walks out sideways beneath the level.
    private const float FloorTop = 0f;
    private const float FloorThickness = 3f;

    private const float TerraceTop = 2f;

    // Openings are sized against the tuning defaults, not by eye: a 0.35 radius
    // is 0.70 across, so 1.4 leaves a body-width of clearance either side, and
    // 2.2 clears a 1.8 stature by more than a head. Both are ordinary
    // architectural numbers, which is the point — a door that only works when
    // it is oversized is not a door.
    private const float OpeningWidth = 1.4f;
    private const float OpeningHeight = 2.2f;

    /// <summary>
    /// Authors the whole course into <paramref name="scene"/> and returns how
    /// many brush nodes it added.
    /// </summary>
    /// <param name="scene">The scene to author into, before its first compile.</param>
    /// <param name="structure">Material for floors, terraces and stairs.</param>
    /// <param name="wall">Material for walls and blocks.</param>
    /// <param name="accent">Material for ramps, cuts and the part brush.</param>
    public static int Build(Scene scene, MaterialRef structure, MaterialRef wall, MaterialRef accent)
    {
        ArgumentNullException.ThrowIfNull(scene);

        int count = 0;

        // --- Ground and perimeter --------------------------------------------
        // 40x40, x in [130,170], z in [-20,20]. The walls are not decoration:
        // without them the first thing a tester does is walk off the edge, and
        // "I fell out of the world" is a bad first impression of a controller
        // that is in fact working.
        count += Box(scene, "Play.Floor",
            new Vector3(150f, FloorTop - FloorThickness * 0.5f, 0f),
            new Vector3(20f, FloorThickness * 0.5f, 20f), structure);

        // Set INWARD from the rim so they stand on the slab rather than beside
        // it. A wall flush with the outer edge is a wall with nothing under it,
        // which looks identical from inside and is a floating slab from
        // everywhere else. Inner faces therefore at x = 131 / 169 and
        // z = -19 / 19.
        count += Box(scene, "Play.WallWest", new Vector3(130.5f, 1.25f, 0f), new Vector3(0.5f, 1.25f, 20f), wall);
        count += Box(scene, "Play.WallEast", new Vector3(169.5f, 1.25f, 0f), new Vector3(0.5f, 1.25f, 20f), wall);
        count += Box(scene, "Play.WallNorth", new Vector3(150f, 1.25f, -19.5f), new Vector3(20f, 1.25f, 0.5f), wall);
        count += Box(scene, "Play.WallSouth", new Vector3(150f, 1.25f, 19.5f), new Vector3(20f, 1.25f, 0.5f), wall);

        // --- Stairs: three rises bracketing StepHeight (0.45) -----------------
        // All three climb exactly 2.0 units over exactly 4.0 of run, so the only
        // variable between them is the rise — which is the whole experiment.
        // 0.25 and 0.40 must climb; 0.50 must refuse, and refusing is the test.
        // A regression that raises the effective step height turns the third
        // staircase into a walkable one, which you notice immediately because
        // you are suddenly standing somewhere you should not be.
        count += Stairs(scene, "Play.StairsGentle", startX: 135f, treads: 8, rise: 0.25f, tread: 0.5f,
            zCenter: -8f, width: 4f, material: structure);
        count += Stairs(scene, "Play.StairsLimit", startX: 135f, treads: 5, rise: 0.40f, tread: 0.8f,
            zCenter: 0f, width: 4f, material: structure);
        count += Stairs(scene, "Play.StairsTooSteep", startX: 135f, treads: 4, rise: 0.50f, tread: 1.0f,
            zCenter: 8f, width: 4f, material: structure);

        // --- The terrace, and the doorway that is the only way across it ------
        // The wall spans the terrace's full width on purpose. Going around it
        // means dropping off the terrace, so the CSG hole is the only way
        // through at this height — which makes "the doorway sealed itself" a
        // dead end you walk into rather than a detail you might not check.
        count += Box(scene, "Play.Terrace",
            new Vector3(143.5f, TerraceTop - 1f, 0f), new Vector3(4.5f, 1f, 12f), structure);

        count += Box(scene, "Play.DoorWall",
            new Vector3(143.5f, TerraceTop + 1.5f, 0f), new Vector3(0.2f, 1.5f, 12f), wall);

        // Flush through the wall's full thickness, exactly like the demo room's
        // door: the ±x planes of the cut coincide with the wall's own. That is
        // the case a naive carve gets wrong, and it is worth having a second
        // instance of it at a size a person can walk through.
        count += Cut(scene, "Play.DoorCut",
            new Vector3(143.5f, TerraceTop + OpeningHeight * 0.5f, 0f),
            new Vector3(0.2f, OpeningHeight * 0.5f, OpeningWidth * 0.5f), accent);

        // --- The tunnel: a hole with a ceiling --------------------------------
        // The doorway proves a capsule fits through a vertical slot. This proves
        // the cover's ceiling is real: the passage is cut through a solid block,
        // so above your head is a subtractive brush's cavity wall and not open
        // air. Jump inside it and you should hit something.
        count += Box(scene, "Play.TunnelBlock",
            new Vector3(146f, TerraceTop + 1.5f, 8f), new Vector3(2f, 1.5f, 3f), wall);

        count += Cut(scene, "Play.TunnelCut",
            new Vector3(146f, TerraceTop + OpeningHeight * 0.5f, 8f),
            new Vector3(2f, OpeningHeight * 0.5f, OpeningWidth * 0.5f), accent);

        // --- Ramps: three angles bracketing MaxSlopeAngleDegrees (46) ---------
        // Same 2.0 rise, same destination, three gradients. 25° and 40° must
        // walk; 55° must slide you back down. The 40° one is the interesting
        // one to walk repeatedly: cresting it is where a ground snap that gates
        // on downward velocity launches you into the air, and that bug is
        // invisible on flat ground.
        count += Box(scene, "Play.RampPlatform",
            new Vector3(159f, TerraceTop - 1f, -13f), new Vector3(3f, 1f, 7f), structure);

        count += Ramp(scene, "Play.Ramp25", 151.71f, 156f, FloorTop, TerraceTop, -17f, 4f, 0.4f, accent);
        count += Ramp(scene, "Play.Ramp40", 153.62f, 156f, FloorTop, TerraceTop, -13f, 4f, 0.4f, accent);
        count += Ramp(scene, "Play.Ramp55", 154.60f, 156f, FloorTop, TerraceTop, -8.5f, 3f, 0.4f, accent);

        // --- The chasm: a hole cut clean through the slab ---------------------
        // Three across, which a 1.2 jump height clears at walk speed with a
        // metre to spare, and cut through the WHOLE thickness rather than part
        // of it. That choice was made by measurement, not taste: at
        // AirSpeedCap = 1.0 a character standing at the bottom of a pit against
        // a vertical wall builds barely a metre per second of air control, and
        // 0.7 of horizontal clearance needs more airtime than a 1-unit jump
        // gives — so any partial-depth pit here would have been a trap that only
        // the respawn could undo. A hole is honest about that instead, and it
        // makes the fall-out guard something a player actually meets.
        //
        // Its top plane is flush with the floor's, deliberately: this is the one
        // subtractive brush in the course that cuts a FLOOR, which is the case
        // that once had a character standing on the invisible cap face of a
        // cover element.
        count += Cut(scene, "Play.ChasmCut",
            new Vector3(153.5f, FloorTop - 1.75f, 10f), new Vector3(1.5f, 1.75f, 8f), accent);

        // --- A part brush to stand on -----------------------------------------
        // The only geometry in the course that reaches the mover through the
        // live spatial-index lane instead of the compiled static world. It is
        // never carved and never carves, and it is at 1.0 so it can be jumped
        // onto from the floor (a 1.2 jump height, with the margin a player
        // needs). If the part lane breaks, this is a solid-looking box you fall
        // straight through.
        count += Part(scene, "Play.PartPlatform",
            new Vector3(164f, 0.8f, -3f), new Vector3(1.5f, 0.2f, 1.5f), accent);

        // --- Pillars: corners, and sliding along a wall -----------------------
        // Deliberately close enough together to walk between and to get wedged
        // in, because the two-and-three-plane contact cases are where a solver
        // either resolves or jitters, and jitter is something you feel long
        // before a test finds it.
        count += Box(scene, "Play.PillarA", new Vector3(162f, 1.5f, 8f), new Vector3(0.5f, 1.5f, 0.5f), wall);
        count += Box(scene, "Play.PillarB", new Vector3(164.2f, 1.5f, 8f), new Vector3(0.5f, 1.5f, 0.5f), wall);
        count += Box(scene, "Play.PillarC", new Vector3(163.1f, 1.5f, 10.2f), new Vector3(0.5f, 1.5f, 0.5f), wall);
        count += Box(scene, "Play.PillarD", new Vector3(167f, 1.5f, 14f), new Vector3(0.5f, 1.5f, 0.5f), wall);

        return count;
    }

    // --- Authoring helpers ---------------------------------------------------
    // Placement on the node, size in the brush — the engine's rule, and the one
    // that keeps CSG precision independent of how far out the course sits.
    // Symmetric extents specifically: CreateBox banks an asymmetric centre in
    // the brush's own Transform, which a node placement then ignores, so the
    // brush would silently sit somewhere other than where it was asked to.

    private static int Box(Scene scene, string name, Vector3 center, Vector3 half, MaterialRef material)
    {
        var node = scene.Root.CreateChild(name);
        node.LocalPosition = center;
        node.Brush = Brush.CreateBox(-half, half, material);
        return 1;
    }

    private static int Cut(Scene scene, string name, Vector3 center, Vector3 half, MaterialRef material)
    {
        var node = scene.Root.CreateChild(name);
        node.LocalPosition = center;
        node.Brush = Brush.CreateBox(-half, half, material).WithOperation(BrushOperation.Subtractive);
        return 1;
    }

    private static int Part(Scene scene, string name, Vector3 center, Vector3 half, MaterialRef material)
    {
        var node = scene.Root.CreateChild(name);
        node.LocalPosition = center;
        node.BrushKind = BrushKind.Part;
        node.Brush = Brush.CreateBox(-half, half, material);
        return 1;
    }

    // One box per tread, each sunk to the floor rather than floating: a stair is
    // a stack of solids, and CSG fuses the shared faces away. Building them as
    // thin slabs instead would leave the risers open underneath, which changes
    // nothing you can see and everything the step probe's downward cast finds.
    private static int Stairs(
        Scene scene, string name, float startX, int treads, float rise, float tread,
        float zCenter, float width, MaterialRef material)
    {
        for (int i = 0; i < treads; i++)
        {
            float top = FloorTop + rise * (i + 1);
            float centerX = startX + tread * i + tread * 0.5f;

            Box(scene, $"{name}{i}",
                new Vector3(centerX, (top + FloorTop - FloorThickness) * 0.5f, zCenter),
                new Vector3(tread * 0.5f, (top - FloorTop + FloorThickness) * 0.5f, width * 0.5f),
                material);
        }

        return treads;
    }

    // A ramp is a rotated box, which is legal precisely because the rotation is
    // rigid — a scaled brush transform is refused by the placement snapshot, and
    // for good reason, but a rotated one is just a different frame.
    //
    // The caller gives the two endpoints of the TOP SURFACE, because that is the
    // surface being tested; the box is then hung underneath it and allowed to
    // sink into the floor, where CSG resolves the join.
    private static int Ramp(
        Scene scene, string name, float startX, float endX, float startY, float endY,
        float zCenter, float width, float thickness, MaterialRef material)
    {
        float dx = endX - startX;
        float dy = endY - startY;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        float angle = MathF.Atan2(dy, dx);

        // Rotation about +z carries local +x to (cos, sin, 0) — up the slope —
        // and local +y to (−sin, cos, 0), which is the surface normal. So the
        // box's own top face IS the ramp surface, at the angle asked for.
        var up = new Vector3(-MathF.Sin(angle), MathF.Cos(angle), 0f);
        var topCenter = new Vector3((startX + endX) * 0.5f, (startY + endY) * 0.5f, zCenter);

        // Long enough underneath to reach the floor slab at every angle, so the
        // shallow ramps do not end up as floating wedges with a gap under the
        // toe. The excess is buried and costs one fused face.
        float depth = thickness + (endY - startY) + 1f;

        var node = scene.Root.CreateChild(name);
        node.LocalPosition = topCenter - up * (depth * 0.5f);
        node.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);

        var half = new Vector3(length * 0.5f, depth * 0.5f, width * 0.5f);
        node.Brush = Brush.CreateBox(-half, half, material);
        return 1;
    }
}
