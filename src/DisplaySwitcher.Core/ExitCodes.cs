namespace DisplaySwitcher;

/// <summary>
/// Process exit codes. Sunshine only distinguishes zero from non-zero, but the specific
/// values make the logs and any wrapping scripts far easier to reason about.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int VerificationFailed = 1;
    public const int MonitorMissing = 2;
    public const int BadArguments = 3;
    public const int UnexpectedError = 4;
}
