namespace SennheiserTray
{
    public static class AncModeRules
    {
        public const int Off = 0;
        public const int Custom = 1;
        public const int Adaptive = 2;

        public static bool IsCustomLevelAvailable(int mode) => mode == Custom;

        public static bool IsAutoEnabled(int mode) => mode == Adaptive;

        public static int ModeForAutoToggle(bool enabled) => enabled ? Adaptive : Off;
    }
}
