namespace PharmaLIMS.Services;

/// <summary>Serializes session teardown; a replacement login can only follow cleared authority.</summary>
public sealed class SessionTransitionCoordinator
{
    private bool _transitioning;

    public static string LoginTitle(bool preparing) =>
        preparing ? "PharmaLIMS - Preparing Sign In" : "PharmaLIMS - Login";

    public static bool CanFinishHandoff(bool childExited, bool hasWindow, string title) =>
        !childExited && hasWindow && title == LoginTitle(preparing: false);


    // Some native prompt exits return no usable credentials. Neither cancellation nor
    // incomplete input may authenticate, launch a process, or restore the previous session.
    public static bool ShouldCancelWindowsSwitch(int result, string? account, int passwordLength) =>
        result == 1223 || (result == 0 && (string.IsNullOrWhiteSpace(account) || passwordLength <= 0));

    public bool TryTransition(bool hasPendingWork, Action clearSession, Action showLogin)
    {
        if (hasPendingWork || _transitioning)
            return false;
        _transitioning = true;
        try
        {
            clearSession();
            showLogin();
            return true;
        }
        finally { _transitioning = false; }
    }
}
