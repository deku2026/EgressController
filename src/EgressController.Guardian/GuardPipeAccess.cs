namespace EgressController.Guardian;

public static class GuardPipeAccess
{
    // Individual file rights, deliberately without FILE_CREATE_PIPE_INSTANCE (0x4).
    // GENERIC_WRITE would implicitly request that bit and deny an ordinary tray client.
    public const uint ClientRights = 0x12019b;
    public const uint ClientFlags = 0x40000000 | 0x00100000 | 0x00010000; // Overlapped; identification only.
    public static string Descriptor(string ownerSid)
        => $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x{ClientRights:x};;;{ownerSid})";
}
