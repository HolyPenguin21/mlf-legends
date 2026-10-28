namespace MLFLegends.Combat
{
    public static class CombatSpatialTuning
    {
        public const float CellSize = 1f;

        // Work/memory guards, never candidate limits. Oversized queries scan the complete snapshot.
        // An unrepresentable/oversized build fails explicitly and leaves no readable snapshot.
        public const int MaxBuildEntries = 16 * 1024 * 1024;
        public const int MaxQueryCells = 64 * 1024;
    }
}
