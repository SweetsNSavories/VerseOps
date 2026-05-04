namespace VerseOps.Models;

/// <summary>
/// Power Platform environment SKU. Maps to the "properties.environmentSku" value
/// expected by the BAP control-plane (e.g. "Sandbox", "Production").
/// </summary>
public enum EnvironmentType
{
    Sandbox,
    Production,
    Developer,
    Trial
}
