namespace SennheiserTray
{
    public static class OrbDialRules
    {
        public static double LevelFromAngle(double angleDegrees)
        {
            double normalizedAngle = ((angleDegrees % 360) + 360) % 360;
            double progress = (normalizedAngle - 135 + 360) % 360;
            if (progress > 270)
            {
                double distanceToMaximum = progress - 270;
                double distanceToMinimum = 360 - progress;
                return distanceToMaximum <= distanceToMinimum ? 100 : 0;
            }

            return Math.Round(progress * 100 / 270);
        }

        public static string LabelForLevel(double level)
        {
            double clamped = Math.Clamp(level, 0, 100);
            return clamped < 34 ? "Quiet" : clamped > 66 ? "Aware" : "Balanced";
        }
    }
}
