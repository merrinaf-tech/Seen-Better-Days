using UnityEngine;
using Game.Serialization;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Prevents runtime visual entities from being rebuilt while the serializer may still be
    /// snapshotting the world.
    ///
    /// The serialize-phase guard removes all of our structural changes before a save. Recreating
    /// them on the next modification frame is too early: the serializer keeps writing in jobs
    /// after its entity table has been created. <see cref="SaveGameSystem.Enabled"/> stays true
    /// until that write dependency has completed, so follow it and keep a short grace period after
    /// the last active frame. This covers automatic passes and manual keys without guessing how
    /// long a small or large city takes to save.
    /// </summary>
    internal static class SaveMutationGate
    {
        private const float ResumeDelaySeconds = 3f;
        private static float s_BlockedUntil;
        private static bool s_SavePendingResume;

        public static bool IsBlocked(SaveGameSystem saveGameSystem)
        {
            float now = Time.realtimeSinceStartup;
            if (saveGameSystem != null && saveGameSystem.Enabled)
            {
                s_BlockedUntil = Mathf.Max(s_BlockedUntil, now + ResumeDelaySeconds);
                s_SavePendingResume = true;
                return true;
            }

            if (now < s_BlockedUntil)
            {
                return true;
            }

            if (s_SavePendingResume)
            {
                s_SavePendingResume = false;
                Mod.Log.Info("Seen Better Days: save write finished and the three-second safety "
                           + "delay elapsed; visual updates may resume.");
            }

            return false;
        }

        public static void BlockAfterSerializationStarts()
        {
            s_BlockedUntil = Mathf.Max(s_BlockedUntil, Time.realtimeSinceStartup + ResumeDelaySeconds);
            s_SavePendingResume = true;
        }
    }
}
