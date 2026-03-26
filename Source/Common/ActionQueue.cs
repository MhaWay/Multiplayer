using System;
using System.Collections.Generic;

namespace Multiplayer.Common
{
    // Uses List<T> instead of Queue<T> because Queue<T> is in System.dll on
    // .NET Framework but in mscorlib on Unity/Mono.  Common compiles against
    // Krafs.Rimworld.Ref (Mono) so the emitted reference targets mscorlib,
    // which causes a TypeLoadException when the Server runs on .NET Framework.
    public class ActionQueue
    {
        private List<Action> queue = new();
        private List<Action> tempQueue = new();

        public void RunQueue(Action<string> errorLogger)
        {
            lock (queue)
            {
                if (queue.Count > 0)
                {
                    tempQueue.AddRange(queue);
                    queue.Clear();
                }
            }

            try
            {
                while (tempQueue.Count > 0)
                {
                    var action = tempQueue[0];
                    tempQueue.RemoveAt(0);
                    action.Invoke();
                }
            }
            catch (Exception e)
            {
                errorLogger($"Exception while executing action queue: {e}");
            }
        }

        public void Enqueue(Action action)
        {
            lock (queue)
                queue.Add(action);
        }
    }
}
