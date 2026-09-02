using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The fourth node payload, and the one rule that makes it safe to duplicate.
/// </summary>
/// <remarks>
/// <b>A shared payload is a bug you find months later.</b> Entity data is
/// mutable, exactly like a light, so a clone that shared the instance would have
/// every edit to the duplicate land on the original as well - and the tree, the
/// viewport and the map file would all look correct while it happened.
/// </remarks>
public sealed class EntityDataTests
{
    private static EntityData Door()
    {
        var entity = new EntityData("func_door");
        entity.SetValue("speed", "100");
        entity.SetValue("wait", "4");
        entity.Connections.Add(new EntityConnection(
            "OnFullyOpen", "hall_light", "TurnOn", "", 0.5f, EntityConnection.Infinite));
        return entity;
    }

    [Fact]
    public void Cloned_entity_data_shares_nothing_with_the_original()
    {
        EntityData original = Door();

        EntityData copy = original.Clone();
        copy.ClassName = "func_button";
        copy.SetValue("speed", "9");
        copy.SetValue("lip", "2");
        copy.Connections.Clear();

        original.ClassName.ShouldBe("func_door");
        original.TryGetValue("speed", out string speed).ShouldBeTrue();
        speed.ShouldBe("100");
        original.TryGetValue("lip", out _).ShouldBeFalse();
        original.Connections.Count.ShouldBe(1);
    }

    [Fact]
    public void A_cloned_node_gets_its_own_entity_data()
    {
        // The payload rule stated at the node level, which is where a duplicate
        // gesture actually goes through.
        var scene = new Scene("Entities");
        SceneNode node = scene.Root.CreateChild("Door");
        node.Entity = Door();

        SceneNode copy = node.Clone();

        copy.Entity.ShouldNotBeNull();
        copy.Entity!.ShouldNotBeSameAs(node.Entity);

        copy.Entity.SetValue("speed", "9");
        copy.Entity.Connections.Clear();

        node.Entity!.TryGetValue("speed", out string speed).ShouldBeTrue();
        speed.ShouldBe("100");
        node.Entity.Connections.Count.ShouldBe(1);
    }

    [Fact]
    public void A_node_carrying_no_entity_clones_without_one()
    {
        var scene = new Scene("Entities");
        SceneNode node = scene.Root.CreateChild("Marker");

        node.Clone().Entity.ShouldBeNull();
    }

    [Fact]
    public void Keyvalues_keep_the_order_they_were_authored_in()
    {
        // Authored order is what the map format writes, so a list rather than a
        // dictionary is the whole point of this type: reshuffling somebody's
        // hand-edited file on save is a defect they cannot even report cleanly.
        var entity = new EntityData("light");
        entity.SetValue("targetname", "hall_light");
        entity.SetValue("color", "1 0.9 0.75");
        entity.SetValue("range", "12");

        entity.Keyvalues.Count.ShouldBe(3);
        entity.Keyvalues[0].Key.ShouldBe("targetname");
        entity.Keyvalues[1].Key.ShouldBe("color");
        entity.Keyvalues[2].Key.ShouldBe("range");
    }

    [Fact]
    public void Rewriting_a_keyvalue_replaces_it_where_it_stands()
    {
        // In place, never remove-and-append: moving an edited member to the end
        // of the object rewrites a region of the file nobody touched.
        var entity = new EntityData("light");
        entity.SetValue("targetname", "hall_light");
        entity.SetValue("range", "12");

        entity.SetValue("targetname", "porch_light");

        entity.Keyvalues.Count.ShouldBe(2);
        entity.Keyvalues[0].Key.ShouldBe("targetname");
        entity.Keyvalues[0].Value.ShouldBe("porch_light");
        entity.Keyvalues[1].Key.ShouldBe("range");
    }

    [Fact]
    public void Keyvalue_names_are_matched_ordinally()
    {
        // A case-folding rule would need a culture to fold in, and the same file
        // would then mean different things on different machines.
        var entity = new EntityData("light");
        entity.SetValue("range", "12");
        entity.SetValue("Range", "40");

        entity.Keyvalues.Count.ShouldBe(2);
        entity.TryGetValue("range", out string lower).ShouldBeTrue();
        lower.ShouldBe("12");
    }

    [Fact]
    public void A_connection_with_a_negative_count_fires_forever()
    {
        new EntityConnection("OnTrigger", "door", "Open", "", 0f, EntityConnection.Infinite)
            .FiresForever.ShouldBeTrue();
        new EntityConnection("OnTrigger", "door", "Open", "", 0f, 1)
            .FiresForever.ShouldBeFalse();
    }
}
