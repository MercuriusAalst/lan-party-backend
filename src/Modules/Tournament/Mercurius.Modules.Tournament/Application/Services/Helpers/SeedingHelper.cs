namespace Mercurius.Modules.Tournament.Application.Services.Helpers;

internal static class SeedingHelper
{
    /// <summary>
    /// Returns the bracket slot for each seed using classic seeding (1 vs P, 2 vs P-1, recursively).
    /// When there are fewer participants than slots, the empty bottom seeds face the top seeds,
    /// so every bye pairs a real participant with an empty slot and no first-round match is empty.
    /// </summary>
    public static int[] GenerateBracketSlotOrder(int slotCount)
    {
        int[] seedAtSlot = [0];
        while (seedAtSlot.Length < slotCount)
        {
            int size = seedAtSlot.Length * 2;
            seedAtSlot = seedAtSlot.SelectMany(seed => new[] { seed, size - 1 - seed }).ToArray();
        }

        int[] slotOfSeed = new int[slotCount];
        for (int slot = 0; slot < slotCount; slot++)
            slotOfSeed[seedAtSlot[slot]] = slot;

        return slotOfSeed;
    }
}
