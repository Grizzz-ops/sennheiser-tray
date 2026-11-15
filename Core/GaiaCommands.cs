namespace SennheiserTray
{
    public static class GaiaCommands
    {
        public const ushort VendorId = 0x0495;

        public const ushort TransparencySet = 0x1804;
        public const ushort AncEnableSet = 0x1A04;
        public const ushort AncModeSet = 0x1A00;
        public const ushort AncLevelSet = 0x1A02;
        public const ushort EqBandSet = 0x1001;
        public const ushort BassBoostSet = 0x1008;
        public const ushort BassBoostGet = 0x1009;
        public const ushort BassBoostReply = 0x1109;
        public const ushort NotificationRegister = 0x0007;
        public const byte BatteryFeature = 0x03;
        public const ushort BatteryGet = 0x0603;
        public const ushort BatteryReply = 0x0703;
        public const ushort BatteryNotification = 0x0683;

        public const ushort AncStateGet = 0; // TODO: verify via packet capture
        public const ushort AncStateReply = 0; // TODO: verify via packet capture
        public const ushort AncLevelGet = 0; // TODO: verify via packet capture
        public const ushort AncLevelReply = 0; // TODO: verify via packet capture
        public const ushort EqBandGet = 0; // TODO: verify via packet capture
        public const ushort EqBandReply = 0; // TODO: verify via packet capture
        public const ushort SidetoneSet = 0; // TODO: verify via packet capture
        public const ushort SidetoneGet = 0; // TODO: verify via packet capture
        public const ushort LowLatencySet = 0; // TODO: verify via packet capture

        public static bool IsVerified(ushort commandId) => commandId != 0;
    }
}
