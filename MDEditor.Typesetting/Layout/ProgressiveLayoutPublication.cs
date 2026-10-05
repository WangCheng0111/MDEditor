namespace MDEditor.Typesetting.Layout;

/// <summary>Allows a completed edit frame to show progress without letting it replace the latest request.</summary>
public static class ProgressiveLayoutPublication
{
    public static bool CanCompleteCurrent(long candidateVersion, long latestVersion,
        double candidateWidth, double requestedWidth, int candidateZoom, int requestedZoom,
        bool latestRequestIsEdit) =>
        latestRequestIsEdit && candidateVersion == latestVersion && candidateWidth == requestedWidth &&
        candidateZoom == requestedZoom;

    public static bool CanPresentIntermediate(long candidateVersion, long latestVersion, long presentedVersion,
        double candidateWidth, double requestedWidth, int candidateZoom, int requestedZoom,
        bool latestRequestIsEdit) =>
        latestRequestIsEdit && candidateVersion > presentedVersion && candidateVersion < latestVersion &&
        candidateWidth == requestedWidth && candidateZoom == requestedZoom;
}
