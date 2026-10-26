namespace Voxta.Modules.Aios.Tools;

/// <summary>An expected input error whose message is safe to return to the model.</summary>
internal sealed class ModuleToolInputException(string message) : Exception(message);
