namespace StalkerGPS.Unreal;

/// <summary>
/// Offsets of the UStruct and FField/FProperty members used to walk reflection data.
/// These differ between engine versions, so they are detected rather than assumed.
/// </summary>
internal sealed record ReflectionLayout(int SuperStruct, int ChildProperties, int FieldNext, int FieldName, int PropertyElementSize, int PropertyOffset)
{
    // UE 5.4+: FFieldVariant shrank to 8 bytes, moving everything after FField::Owner up by 8.
    // UE 4.25 – 5.3: 16-byte FFieldVariant.
    private static readonly ReflectionLayout[] Candidates =
    [
        new(SuperStruct: 0x40, ChildProperties: 0x50, FieldNext: 0x18, FieldName: 0x20, PropertyElementSize: 0x34, PropertyOffset: 0x44),
        new(SuperStruct: 0x40, ChildProperties: 0x50, FieldNext: 0x20, FieldName: 0x28, PropertyElementSize: 0x3C, PropertyOffset: 0x4C),
    ];

    /// <summary>Picks the layout under which SceneComponent has a 24-byte RelativeLocation (FVector of doubles).</summary>
    public static bool TryDetect(UnrealResolver resolver, nint sceneComponentClass, out ReflectionLayout layout)
    {
        foreach (var candidate in Candidates)
        {
            var field = candidate.FindField(resolver, sceneComponentClass, "RelativeLocation", searchSupers: false);
            if (field != 0 && resolver.Memory.ReadInt32(field + candidate.PropertyElementSize) == 24)
            {
                layout = candidate;
                return true;
            }
        }
        layout = Candidates[0];
        return false;
    }

    /// <summary>Returns the byte offset of a property within instances of <paramref name="cls"/>, or -1.</summary>
    public int FindPropertyOffset(UnrealResolver resolver, nint cls, string name)
    {
        var field = FindField(resolver, cls, name, searchSupers: true);
        return field == 0 ? -1 : resolver.Memory.ReadInt32(field + PropertyOffset);
    }

    private nint FindField(UnrealResolver resolver, nint cls, string name, bool searchSupers)
    {
        var mem = resolver.Memory;
        for (var s = cls; s != 0; s = searchSupers ? mem.ReadPointer(s + SuperStruct) : 0)
        {
            // Bounded walk in case a wrong layout candidate leads into garbage.
            var field = mem.ReadPointer(s + ChildProperties);
            for (int i = 0; field != 0 && i < 2000; i++, field = mem.ReadPointer(field + FieldNext))
            {
                if (resolver.Name(mem.ReadInt32(field + FieldName)) == name)
                    return field;
            }
        }
        return 0;
    }
}
