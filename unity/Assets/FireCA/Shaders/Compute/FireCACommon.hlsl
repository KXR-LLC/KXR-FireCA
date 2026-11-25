// Shared helpers of the FireCA compute shaders and the terrain shader.
#ifndef FIRECA_COMMON_INCLUDED
#define FIRECA_COMMON_INCLUDED

// The Arrival Time value of a cell that is not ignited.
#define FIRECA_NOT_IGNITED 1.0e30

// The Fuel texture is R8_UNorm. The fuel code is the stored byte (docs/decisions.md, decision D4).
uint FireCA_LoadFuelCode(Texture2D<float> fuel, int2 cell)
{
    return (uint)(fuel.Load(int3(cell, 0)) * 255.0 + 0.5);
}

bool FireCA_IsBurnable(uint fuelCode)
{
    return fuelCode >= 1u && fuelCode <= 13u;
}

#endif
