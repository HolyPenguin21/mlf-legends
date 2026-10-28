namespace MLFLegends.Combat
{
    /// <summary>Technical starting calibration, not final balance. Spawn is the only consumer of Default.</summary>
    public static class CombatBodyTuning
    {
        public const float Radius = 0.3f;
        public const float Height = 2f;
        public const float Mass = 1f;
        public static CombatBody Default => new CombatBody { Radius = Radius, Height = Height, Mass = Mass };
    }
}
