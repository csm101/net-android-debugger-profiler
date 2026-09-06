namespace TestTarget.Support;

/// <summary>
/// Work the app does through another assembly, so a session has methods from two modules
/// to name, symbolicate and weave.
/// </summary>
public static class SupportWork
{
    /// <summary>Deterministic and cheap: it must show up without dominating the profile.</summary>
    public static long Checksum(int rounds)
    {
        long acc = 0;
        for (int i = 1; i <= rounds; i++)
            acc += Mix(i);
        return acc;
    }

    private static long Mix(int value) => (value * 2654435761L) & 0xFFFFFF;
}
