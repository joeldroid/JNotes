namespace JNotes.Core.Users;

/// <inheritdoc cref="ISetupStateProvider"/>
public sealed class SetupStateProvider : ISetupStateProvider
{
    public bool IsSetupComplete { get; private set; }

    /// <summary>Called once by <c>HostExtensions.InitializeSetupStateAsync</c> at startup.</summary>
    internal void SetSetupComplete(bool isComplete) => IsSetupComplete = isComplete;
}
