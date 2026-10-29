using System.Collections.Generic;
using UnityEngine;

namespace MoonSweepersTwitch.Util
{
    /// <summary>
    /// Minimal IMGUI toast overlay. Drawn by <see cref="ModBehaviour.OnGUI"/>.
    /// Gives immediate, game-independent proof that an effect fired — useful before
    /// (and after) you wire effects into the real game systems.
    /// </summary>
    public static class OnScreenMessages
    {
        private readonly struct Toast
        {
            public readonly string Text;
            public readonly float ExpiresAt;
            public Toast(string text, float expiresAt)
            {
                Text = text;
                ExpiresAt = expiresAt;
            }
        }

        private static readonly object Gate = new object();
        private static readonly List<Toast> Toasts = new List<Toast>();
        private static GUIStyle? _style;

        public static bool Enabled { get; set; } = true;

        /// <summary>Show a toast for <paramref name="seconds"/>. Safe to call from the main thread.</summary>
        public static void Show(string text, float seconds = 5f)
        {
            if (!Enabled) return;
            lock (Gate)
            {
                Toasts.Add(new Toast(text, Time.realtimeSinceStartup + seconds));
                // Keep the stack from growing without bound during a bits storm.
                if (Toasts.Count > 8) Toasts.RemoveAt(0);
            }
        }

        public static void Draw()
        {
            if (!Enabled) return;

            // Some IL2CPP builds strip the IMGUI control-id plumbing GUI.Box relies on
            // (GUIUtility.GetControlID -> "Method unstripping failed"), which would throw
            // every frame once a toast is queued. If drawing ever fails, disable the
            // overlay for good (and log once) instead of spamming the error.
            try
            {
                DrawInternal();
            }
            catch (System.Exception ex)
            {
                Enabled = false;
                lock (Gate) Toasts.Clear();
                Plugin.Log.LogWarning(
                    "[OnScreenMessages] On-screen toasts disabled (IMGUI unavailable on this build): "
                    + ex.Message + ". Effects still run; watch the BepInEx log for confirmation.");
            }
        }

        private static void DrawInternal()
        {
            float now = Time.realtimeSinceStartup;
            _style ??= new GUIStyle(GUI.skin.box)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                normal = { textColor = Color.white },
                // IL2CPP interop doesn't expose RectOffset's 4-arg ctor; set the fields instead.
                padding = new RectOffset { left = 10, right = 10, top = 6, bottom = 6 },
            };

            lock (Gate)
            {
                Toasts.RemoveAll(t => t.ExpiresAt <= now);

                float y = 12f;
                const float width = 420f;
                for (int i = 0; i < Toasts.Count; i++)
                {
                    float x = Screen.width - width - 12f;
                    GUI.Box(new Rect(x, y, width, 44f), Toasts[i].Text, _style);
                    y += 50f;
                }
            }
        }
    }
}
