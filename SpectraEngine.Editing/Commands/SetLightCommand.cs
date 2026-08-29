using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Writes a node's light settings.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>Light</c> is the one MUTABLE payload, which is exactly why this
/// captures values rather than the object.</b> A brush is immutable, so a brush
/// command can hold the instance itself and swapping it back is a complete
/// undo. A light is not: holding a reference and "restoring" it would restore a
/// pointer to an object whose fields the redo had already overwritten, so undo
/// would appear to do nothing. The five settings are copied by value on both
/// sides.
/// </para>
/// <para>
/// <b>The node's light instance is edited in place rather than replaced.</b>
/// <c>SceneNode.Light</c>'s setter early-outs on reference equality and
/// otherwise re-registers the node with the scene's light list; assigning a
/// fresh instance per keystroke-committed edit would churn that list for a
/// value change it does not care about.
/// </para>
/// <para>
/// <b>Range is validated by the property setter and can refuse.</b>
/// <c>Light.Range</c> throws on anything not strictly positive, so a command
/// carrying zero would throw from inside <c>Do</c>, halfway through a
/// transaction. The caller is responsible for not building one; this type does
/// not silently clamp, because a clamp would write a number the user did not
/// ask for and report nothing.
/// </para>
/// </remarks>
public sealed class SetLightCommand : IEditorCommand
{
    /// <summary>The five settings a light carries, as a value.</summary>
    public readonly record struct Settings(
        LightKind Kind, Vector3 Color, float Intensity, float Range, bool Enabled)
    {
        /// <summary>Reads the current settings off a light.</summary>
        public static Settings From(Light light)
        {
            ArgumentNullException.ThrowIfNull(light);
            return new Settings(light.Kind, light.Color, light.Intensity, light.Range, light.Enabled);
        }

        /// <summary>Writes these settings onto a light.</summary>
        public void ApplyTo(Light light)
        {
            ArgumentNullException.ThrowIfNull(light);
            light.Kind = Kind;
            light.Color = Color;
            light.Intensity = Intensity;
            light.Range = Range;
            light.Enabled = Enabled;
        }
    }

    private WeakReference<SceneNode>? _lastApplied;

    /// <summary>Creates a command from explicit before/after settings.</summary>
    public SetLightCommand(Guid nodeId, Settings before, Settings after)
    {
        NodeId = nodeId;
        Before = before;
        After = after;
    }

    /// <summary>
    /// Captures <paramref name="node"/>'s current light settings as the
    /// before-state. Call this <em>before</em> applying the edit.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node carries no light.</exception>
    public static SetLightCommand Capture(SceneNode node, Settings after)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Light is not { } light)
        {
            throw new InvalidOperationException(
                $"Node '{node.Name}' carries no light to edit.");
        }

        return new SetLightCommand(node.Id, Settings.From(light), after);
    }

    /// <summary>The id of the node this command edits.</summary>
    public Guid NodeId { get; }

    /// <summary>The settings the light carried before the edit.</summary>
    public Settings Before { get; }

    /// <summary>The settings the light carries after the edit.</summary>
    public Settings After { get; }

    /// <inheritdoc/>
    public string Name { get; init; } = "Light";

    /// <inheritdoc/>
    public void Do(Scene scene) => Apply(scene, After);

    /// <inheritdoc/>
    public void Undo(Scene scene) => Apply(scene, Before);

    /// <inheritdoc/>
    public void RollBack(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (scene.TryFindById(NodeId, out SceneNode? node) && node.Light is { } live)
        {
            Before.ApplyTo(live);
            return;
        }

        if (_lastApplied is not null
            && _lastApplied.TryGetTarget(out SceneNode? detached)
            && detached.Light is { } orphan)
        {
            Before.ApplyTo(orphan);
        }
    }

    private void Apply(Scene scene, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // Missing target = no-op, per the IEditorCommand contract. A node whose
        // light was removed since is the same case: there is nothing to write.
        if (!scene.TryFindById(NodeId, out SceneNode? node) || node.Light is not { } light)
            return;

        _lastApplied ??= new WeakReference<SceneNode>(node);
        settings.ApplyTo(light);
    }
}
