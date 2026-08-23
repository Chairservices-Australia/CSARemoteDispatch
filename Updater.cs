using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    public class Updater : MonoBehaviour
    {
        private static int mainThreadId;
        private static Updater? instance;

        public void Start()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            instance = this;
            StartCoroutine(CheckPlayerTransformCoro());
            StartCoroutine(CheckTrainsetsCoro());
            StartCoroutine(DeferredEventsCoro());
            StartCoroutine(Routing.RetryPendingCoroutine());
            StartCoroutine(SpeedSigns.DiscoveryCoroutine());
        }

        private static GameObject? rootObject;

        public static void Create()
        {
            if (rootObject == null)
            {
                rootObject = new GameObject();
                GameObject.DontDestroyOnLoad(rootObject);
                rootObject.AddComponent<Updater>();
            }
        }

        public static void Destroy()
        {
            instance = null;
            if (rootObject != null)
            {
                GameObject.Destroy(rootObject);
                rootObject = null;
            }
        }

        /// Run work that has to happen on the game thread but is too big for one
        /// frame, letting it yield between slices. Must be called from the game
        /// thread. Returns false when there is nothing to run it on - before the
        /// mod has finished starting, or after it has been shut down.
        public static bool RunSliced(IEnumerator routine)
        {
            if (instance == null)
                return false;
            instance.StartCoroutine(routine);
            return true;
        }

        private IEnumerator CheckPlayerTransformCoro()
        {
            var wait = WaitFor.Seconds(0.1f);
            while (true)
            {
                yield return wait;
                PlayerData.CheckTransform();
            }
        }

        private IEnumerator CheckTrainsetsCoro()
        {
            var wait = WaitFor.Seconds(0.1f);
            while (true)
            {
                foreach (var trainset in Trainset.allSets)
                {
                    if (!trainset.firstCar.isStationary)
                    {
                        CarUpdater.MarkTrainsetAsDirty(trainset);
                    }
                }
                // The web UI renders at 10 Hz. Scanning every game frame only
                // repeats the same work and increases contention with the
                // multiplayer update loop without producing a visible update.
                yield return wait;
            }
        }

        private IEnumerator DeferredEventsCoro()
        {
            while (true)
            {
                // A burst of HTTP/network work must not monopolize a frame.
                // Remaining actions stay queued for the next frame.
                var remaining = 64;
                while (remaining-- > 0 && taskQueue.TryDequeue(out var action))
                    action();
                yield return null;
            }
        }

        private static readonly ConcurrentQueue<Action> taskQueue = new ConcurrentQueue<Action>();

        public static Task RunOnMainThread(Action action)
        {
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
            {
                action();
                return Task.CompletedTask;
            }
            var tcs = new TaskCompletionSource<bool>();
            taskQueue.Enqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                }
            });
            return tcs.Task;
        }

        public static Task<T> RunOnMainThread<T>(Func<T> func)
        {
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
            {
                try
                {
                    return Task.FromResult(func());
                }
                catch (Exception e)
                {
                    var failed = new TaskCompletionSource<T>();
                    failed.SetException(e);
                    return failed.Task;
                }
            }
            var tcs = new TaskCompletionSource<T>();
            taskQueue.Enqueue(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                }
            });
            return tcs.Task;
        }
    }
}
