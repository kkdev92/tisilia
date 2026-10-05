namespace Tisilia.Tool;

/// <summary>Exit codes. The first failing stage decides the code; every diagnostic stays in the structured output.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int ConfigOrSchema = 2;
    public const int SemanticOrUnsupported = 3;
    public const int DiffMismatch = 4;
    public const int ConformanceFailed = 5;
    public const int ExternalProcessFailed = 6;
    public const int SafetyPolicyViolation = 7;
}
