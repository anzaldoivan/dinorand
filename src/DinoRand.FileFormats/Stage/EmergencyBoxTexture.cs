namespace DinoRand.FileFormats.Stage;

/// <summary>
/// The colour of an emergency box's model (EMERGENCY-BOX-DATA.md "Box model and texture"). A box is part of
/// its room's static geometry, and its colour is only on the front picture: the red, green and yellow boxes
/// are the same artwork, whose colour sits in three CLUT entries (body, bar fill, bar edge) that no other face
/// of the room samples; the rest is a grey ramp. So a box is recoloured by copying those three entries from a
/// vanilla box of the target colour, with no texture or UV edit. Read from every English room and its stage
/// A/B/C copy, which share their base room's CLUT (static only, not yet seen in game).
/// </summary>
public static class EmergencyBoxTexture
{
    /// <summary>The CLUT origin and the body / fill / edge entry indices of a box's front picture. 4bpp boxes have
    /// a 16-entry CLUT of their own; St502 (ids 3 and 10) and st610 (id 7) are 8bpp, on the room's shared row.
    /// <see cref="Fill"/> and <see cref="Edge"/> are -1 where only the body entry is the box's alone.</summary>
    public readonly record struct Roles(int ClutX, int ClutY, int Body, int Fill, int Edge)
    {
        public bool IsComplete => Fill >= 0 && Edge >= 0;
    }

    /// <summary>Roles by the box id that sits at the location in vanilla (<see cref="EmergencyBoxRecord.OriginalBoxId"/>).
    /// ST60D (id 15) has its own lower-resolution picture on a shared 8bpp page: its bar entries are sampled by
    /// other faces, so only its body can change.</summary>
    public static readonly IReadOnlyList<Roles> ByBoxId = new Roles[]
    {
        new(816, 509, 3, 1, 15),   // 0  st113
        new(848, 509, 3, 1, 15),   // 1  st205
        new(768, 507, 3, 1, 15),   // 2  st306
        new(768, 506, 21, 222, 171), // 3  St502, 8bpp
        new(800, 507, 3, 1, 15),   // 4  st50C
        new(816, 507, 3, 1, 15),   // 5  st602
        new(880, 507, 1, 14, 11),  // 6  st608
        new(768, 506, 36, 241, 225), // 7  st610, 8bpp
        new(832, 508, 4, 15, 14),  // 8  st105
        new(848, 508, 4, 15, 14),  // 9  st301
        new(768, 506, 65, 234, 208), // 10 St502, 8bpp
        new(816, 507, 4, 15, 14),  // 11 st50C
        new(800, 507, 4, 15, 14),  // 12 st608
        new(928, 506, 12, 15, 14), // 13 st104
        new(800, 507, 12, 15, 14), // 14 st602
        new(768, 507, 122, -1, -1), // 15 st60D, 8bpp, body only
        new(960, 507, 12, 15, 14), // 16 st612
    };

    /// <summary>
    /// The CLUT writes that give the box at vanilla location <paramref name="locationId"/> in <paramref name="room"/>
    /// the front-picture colours of the vanilla box <paramref name="donorId"/> in <paramref name="donorRoom"/>, for
    /// <see cref="RoomFile.SetClutEntry"/>. Each entry keeps its own STP bit. Null when the location's roles are
    /// incomplete or a CLUT entry cannot be found in either package.
    /// </summary>
    public static (int ClutX, int ClutY, int Index, ushort Value)[]? PlanRecolour(
        RoomFile room, int locationId, RoomFile donorRoom, int donorId)
    {
        var to = ByBoxId[locationId];
        var from = ByBoxId[donorId];
        if (!to.IsComplete || !from.IsComplete) return null;
        var pairs = new[] { (to.Body, from.Body), (to.Fill, from.Fill), (to.Edge, from.Edge) };
        var writes = new (int, int, int, ushort)[pairs.Length];
        for (int i = 0; i < pairs.Length; i++)
        {
            if (room.ReadClutEntry(to.ClutX, to.ClutY, pairs[i].Item1) is not { } old
                || donorRoom.ReadClutEntry(from.ClutX, from.ClutY, pairs[i].Item2) is not { } colour)
                return null;
            writes[i] = (to.ClutX, to.ClutY, pairs[i].Item1, (ushort)((colour & 0x7FFF) | (old & 0x8000)));
        }
        return writes;
    }
}
