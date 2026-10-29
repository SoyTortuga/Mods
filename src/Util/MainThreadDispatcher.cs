using System;
using System.Collections.Generic;

namespace MoonSweepersTwitch.Util
{
    /// <summary>
    /// Thread-safe hand-off from background threads (the Twitch WebSocket) to
    /// Unity's main thread. Unity objects may only be touched on the main thread,
    /// so effect callbacks are queued here and drained each frame by
    /// <see cref="ModBehaviour"/>.
    /// </summary>
    public static class MainThreadDispatcher
    {
        private static readonly object Gate = new object();
        private static readonly Queue<Action> Queue = new Queue<Action>();

        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (Gate)
            {
                Queue.Enqueue(action);
            }
        }

        /// <summary>Runs all queued actions. Call only from the Unity main thread.</summary>
        public static void Drain()
        {
            while (true)
            {
                Action action;
                lock (Gate)
                {
                    if (Queue.Count == 0) return;
                    action = Queue.Dequeue();
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Error running queued action: " + ex);
                }
            }
        }
    }
}
