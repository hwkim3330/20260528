namespace EthernetPacketGenerator.Models;

public class ValidationResult
{
    public ValidationSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public int BlockIndex { get; init; }

    public static ValidationResult None => new() { Severity = ValidationSeverity.None };

    public static ValidationResult Warning(string msg, int blockIdx = -1) =>
        new() { Severity = ValidationSeverity.Warning, Message = msg, BlockIndex = blockIdx };

    public static ValidationResult Error(string msg, int blockIdx = -1) =>
        new() { Severity = ValidationSeverity.Error, Message = msg, BlockIndex = blockIdx };
}
