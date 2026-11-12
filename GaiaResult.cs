namespace SennheiserTray
{
    public sealed record GaiaResult(bool Success, string Log, byte[]? Payload);
}
