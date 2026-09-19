using UnityEngine;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Prevents runtime visual entities from being rebuilt while the serializer may still be
    /// snapshotting the world.
    ///
    /// The serialize-phase guard removes all of our structural changes before a save. Recreating
    /// them on the next modification frame is too early: a measured crash followed a save-strip
    /// by 38 ms, when Ctrl+Alt+F1 immediately changed the selected building. Unity then died in
    /// native code with no managed stack. A short real-time quarantine covers both manual keys and
    /// automatic passes without trying to infer the serializer's private completion state.
    /// </summary>
    internal static class SaveMutationGate
    {
        private const float ResumeDelaySeconds = 3f;
        private static float s_BlockedUntil;

        public static bool IsBlocked
        {
            get { return Time.realtimeSinceStartup < s_BlockedUntil; }
        }

        public static void BlockAfterSerializationStarts()
        {
            s_BlockedUntil = Mathf.Max(s_BlockedUntil, Time.realtimeSinceStartup + ResumeDelaySeconds);
        }
    }
}
