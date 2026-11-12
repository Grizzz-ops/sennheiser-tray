namespace SennheiserTray
{
    public static class OrbDialRules
    {
        public static double LevelFromAngle(double angleDegrees)
        {
            double normalizedAngle = ((angleDegrees % 360) + 360) % 360;
            double progress = (normalizedAngle - 135 + 360) % 360;
            return Math.Round(Math.Clamp(progress, 0, 270) * 100 / 270);
        }

        public static string LabelForLevel(double level)
        {
            double clamped = Math.Clamp(level, 0, 100);
            return clamped < 34 ? "Quiet" : clamped > 66 ? "Aware" : "Balanced";
        }
    }
}
