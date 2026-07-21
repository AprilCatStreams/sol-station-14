using Content.Shared.Decals;

namespace Content.MapRenderer.Painters;

public readonly record struct DecalData(Decal Decal, uint Index, float X, float Y);
