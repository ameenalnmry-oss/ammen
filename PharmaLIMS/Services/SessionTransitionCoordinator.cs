namespace PharmaLIMS.Services;

/// <summary>Serializes session teardown; a replacement login can only follow cleared authority.</summary>
public sealed class SessionTransitionCoordinator
{
    private bool _transitioning;

    public static string LoginTitle(bool preparing) =>
        preparing ? "PharmaLIMS - Preparing Sign In" : "PharmaLIMS - Login";

    public static bool CanFinishHandoff(bool childExited, bool hasWindow, string title) =>
        !childExited && hasWindow && title == LoginTitle(preparing: false);


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
